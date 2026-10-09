"""Operator-attended identification using the pinned LeRobot motor interface.

Private setup console only; not exposed by the task API. Fixed small goals,
runtime output/speed caps, fresh camera/effort feedback, and no EEPROM writes.
Stored motor limits are additional guards, not approved mechanical calibration.
"""
import argparse
import contextlib
from datetime import datetime, timezone
import fcntl
import json
import os
from pathlib import Path
import signal
import sys
import threading
import time

sys.path.insert(0, "/opt/xur")
STATE = Path("/state/.build/motor-identification")
OUTPUT_CAP = 40  # 40/1000 drive-output limit; not a measured contact-force limit.
LOAD_CUTOFF = OUTPUT_CAP + 10
DISPLACEMENT = 24  # 2.109 degrees at 4096 counts/revolution.
MOVEMENT_ENVELOPE = 32  # 2.813 degrees, including measured overshoot.
SPEED_CAP = 30
ACTIVE_SECONDS = 2.4
JOINTS = ("shoulder_pan", "shoulder_lift", "elbow_flex", "wrist_flex", "wrist_roll", "gripper")
SELECTIONS = {f"{arm}-{joint.replace('_', '-')}": (bus, i+1)
              for arm, bus in (("left", 1), ("right", 0)) for i, joint in enumerate(JOINTS)}
SELECTIONS.update({"head-pan": (1, 7), "head-tilt": (1, 8)})
SETTINGS = ("Torque_Limit", "Acceleration", "Goal_Velocity")


def feedback(bus, name):
    fields = {"position": "Present_Position", "load": "Present_Load", "current": "Present_Current",
              "temperature": "Present_Temperature", "voltage": "Present_Voltage", "torque": "Torque_Enable", "status": "Status"}
    return {key: bus.read(register, name, normalize=False) for key, register in fields.items()}


def check_feedback(value, start, active=False):
    if abs(value["position"] - start) > MOVEMENT_ENVELOPE:
        raise RuntimeError("Joint exceeded the identification movement envelope")
    if abs(value["load"]) >= LOAD_CUTOFF or abs(value["current"]) >= 30:
        raise RuntimeError(f"Motor effort reached the identification limit: load={value['load']}, current={value['current']}")
    if value["temperature"] >= 45:
        raise RuntimeError(f"Motor reported {value['temperature']} degrees C; identification temperature limit is 45")
    if not 110 <= value["voltage"] <= 130 or value["status"] != 0:
        raise RuntimeError("Motor supply or status changed during identification")
    if value["torque"] != int(active):
        raise RuntimeError(f"Unexpected torque-enable state: expected {int(active)}, received {value['torque']}")


def write_checked(bus, name, key, value):
    bus.write(key, name, value, normalize=False)
    if bus.read(key, name, normalize=False) != value:
        raise RuntimeError(f"{key} did not read back as {value}")


def preflight(bus, name):
    value = feedback(bus, name)
    check_feedback(value, value["position"])
    mode = bus.read("Operating_Mode", name, normalize=False)
    low = bus.read("Min_Position_Limit", name, normalize=False)
    high = bus.read("Max_Position_Limit", name, normalize=False)
    if mode != 0 or not 16 <= value["position"] <= 4079 or not low+MOVEMENT_ENVELOPE <= value["position"] <= high-MOVEMENT_ENVELOPE:
        raise RuntimeError("Joint is not in position mode or has insufficient configured range; do not overwrite its limits")
    return value


def probe(bus, name, vision, sleep=time.sleep, clock=time.monotonic):
    """Exactly one positional motor; wheel names/IDs cannot enter this function."""
    if name not in JOINTS and name not in ("head_pan", "head_tilt"):
        raise ValueError("Identification is restricted to arm/head joints")
    initial = preflight(bus, name)
    start = initial["position"]
    saved = {key: bus.read(key, name, normalize=False) for key in SETTINGS}
    result = {"initial": initial, "initialGoal": bus.read("Goal_Position", name, normalize=False),
              "savedRuntimeSettings": saved, "driveOutputCapRaw": OUTPUT_CAP,
              "loadCutoffRaw": LOAD_CUTOFF, "currentCutoffRaw": 30, "temperatureCutoffCelsius": 45,
              "maximumGoalDeltaCounts": DISPLACEMENT, "samples": []}
    changed = False
    disabled = False
    try:
        vision("before")  # Requires fresh camera feedback before any writes.
        changed = True
        result["stage"] = "seed-with-zero-output"
        # STS3215 firmware 3.10 re-enables torque on a position write, even when
        # Torque_Enable was zero. Zero the output before replacing a stale goal.
        write_checked(bus, name, "Torque_Limit", 0)
        write_checked(bus, name, "Goal_Position", start)
        write_checked(bus, name, "Torque_Enable", 0)
        for key, value in (("Acceleration", 1), ("Goal_Velocity", SPEED_CAP)):
            write_checked(bus, name, key, value)
        result["preparedFeedback"] = feedback(bus, name)
        check_feedback(result["preparedFeedback"], start)
        write_checked(bus, name, "Torque_Limit", OUTPUT_CAP)
        began = clock()
        result["monotonicStartedAt"] = began
        result["stage"] = "active"
        bus.write("Torque_Enable", name, 1, normalize=False)
        returning = False
        while clock()-began < ACTIVE_SECONDS:
            elapsed = clock()-began
            value = feedback(bus, name)
            result["samples"].append({"elapsed": elapsed, **value})
            check_feedback(value, start, active=True)
            if bus.read("Torque_Limit", name, normalize=False) != OUTPUT_CAP:
                raise RuntimeError("Runtime drive-output cap changed")
            vision(None)  # Freshness only; image processing stays outside this loop.
            if elapsed >= ACTIVE_SECONDS/2 and not returning:
                vision("displaced")
                bus.write("Goal_Position", name, start, normalize=False)
                returning = True
            elif not returning:
                goal = start + min(DISPLACEMENT, 2 + 2*int(elapsed/0.05))
                bus.write("Goal_Position", name, goal, normalize=False)
            sleep(0.015)
        bus.write("Torque_Enable", name, 0, normalize=False)
        disabled = bus.read("Torque_Enable", name, normalize=False) == 0
        if not disabled:
            raise RuntimeError("Could not verify torque disabled; cut motor power")
        sleep(0.1)
        vision("after")
        result["final"] = feedback(bus, name)
        check_feedback(result["final"], start)
        result["state"] = "completed"
    except BaseException as error:
        result["state"] = "aborted"
        result["error"] = str(error)
    finally:
        if changed:
            cleanup_errors = []
            try:
                write_checked(bus, name, "Torque_Enable", 0)
                write_checked(bus, name, "Torque_Limit", 0)
                # Leave a measured goal, never restore the stale zero goal.
                # This write auto-enables torque, so keep output at zero until
                # all goal writes finish and explicitly disable it afterward.
                position = bus.read("Present_Position", name, normalize=False)
                write_checked(bus, name, "Goal_Position", position)
                for key, value in saved.items():
                    if key != "Torque_Limit":
                        write_checked(bus, name, key, value)
                write_checked(bus, name, "Torque_Enable", 0)
                write_checked(bus, name, "Torque_Limit", saved["Torque_Limit"])
            except BaseException as error:
                cleanup_errors.append(str(error))
            finally:
                # No goal/settings writes after this final torque-off check.
                disabled = False
                try:
                    bus.write("Torque_Enable", name, 0, normalize=False, num_retry=1)
                    disabled = bus.read("Torque_Enable", name, normalize=False) == 0
                    sleep(0.05)
                    result["cleanupFeedback"] = feedback(bus, name)
                    disabled = disabled and result["cleanupFeedback"]["torque"] == 0
                    if not disabled:
                        raise RuntimeError("Final torque-off readback failed")
                except BaseException as error:
                    disabled = False
                    cleanup_errors.append(str(error))
            if cleanup_errors:
                result["state"] = "power-cut-required"
                result["cleanupError"] = "; ".join(cleanup_errors)
                print("CUT MOTOR POWER: could not verify cleanup", file=sys.stderr, flush=True)
        result["torqueOffVerified"] = disabled
    return result


class CameraObserver:
    def __init__(self):
        self.stop = threading.Event()
        self.lock = threading.Lock()
        self.frames = {}
        self.saved = {}
        self.errors = []
        self.threads = [threading.Thread(target=self.capture, args=(name,), daemon=True) for name in ("head", "hand")]
        for thread in self.threads:
            thread.start()
        deadline = time.monotonic()+10
        while len(self.frames) != 2 and not self.errors and time.monotonic() < deadline:
            time.sleep(0.05)
        self.observe("ready")

    def capture(self, name):
        import bridge
        try:
            with bridge.opened_camera(name, full_resolution=True) as camera:
                while not self.stop.is_set():
                    success, frame = camera.read()
                    if not success:
                        raise RuntimeError("Camera stopped returning frames")
                    with self.lock:
                        self.frames[name] = (time.monotonic(), frame)
        except BaseException as error:
            with self.lock:
                self.errors.append(str(error))

    def observe(self, phase):
        with self.lock:
            if self.errors or len(self.frames) != 2 or any(time.monotonic()-value[0] > 0.25 for value in self.frames.values()):
                raise RuntimeError("Camera feedback is missing or stale")
            if phase:
                self.saved[phase] = {name: (stamp, frame.copy()) for name, (stamp, frame) in self.frames.items()}

    def finish(self, directory):
        import bridge
        self.stop.set()
        for thread in self.threads:
            thread.join(timeout=2)
        observations = {}
        for phase, cameras in self.saved.items():
            observations[phase] = {}
            for name, (stamp, frame) in cameras.items():
                import base64
                (directory / f"{name}-{phase}.jpg").write_bytes(base64.b64decode(bridge.encoded_camera_frame(frame)))
                observations[phase][name] = {"monotonicCapturedAt": stamp, **bridge.detect_frame(frame)}
        return observations


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("joint", choices=SELECTIONS)
    parser.add_argument("--operator-present", action="store_true", required=True)
    args = parser.parse_args()
    from discover_buses import inventory
    from lerobot.motors import Motor, MotorNormMode
    from lerobot.motors.feetech import FeetechMotorsBus
    STATE.mkdir(parents=True, exist_ok=True)
    directory = STATE / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S")+"-"+args.joint)
    directory.mkdir()
    buses = []
    observer = None
    pid_path = STATE.parent / "adapter.json"
    owns_pid = False
    lease = None
    result = {"selection": args.joint, "calibrationApproved": False, "wheelCommandsIssued": False}
    reply_mismatches = []
    try:
        lease = (STATE.parent / "adapter.lock").open("w")
        with contextlib.redirect_stdout(sys.stderr):
            fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
            started = Path("/proc/self/stat").read_text().split(") ", 1)[1].split()[19]
            pid_path.write_text(json.dumps({"pid": os.getpid(), "started": started}))
            owns_pid = True
            for index, count in ((0, 9), (1, 8)):
                port = "/dev/arm_right" if index == 0 else "/dev/arm_left"
                models = inventory(port)
                if models != {i: 777 for i in range(1, count+1)}:
                    raise RuntimeError("Inventories no longer match the selected upstream robot")
                names = {i: JOINTS[i-1] if i <= 6 else f"wheel_{i}" if index == 0 else "head_pan" if i == 7 else "head_tilt" for i in models}
                bus = FeetechMotorsBus(port, {names[i]: Motor(i, "sts3215", MotorNormMode.DEGREES) for i in models})
                buses.append(bus)
                from bridge import validate_feetech_replies
                validate_feetech_replies(bus, reply_mismatches)
                bus.connect(handshake=False)
                bus.set_baudrate(1_000_000)
                bus.set_timeout(30)
                if any((bus.read("Firmware_Major_Version", name, normalize=False),
                        bus.read("Firmware_Minor_Version", name, normalize=False)) != (3, 10) for name in bus.motors):
                    raise RuntimeError("Identification is currently validated only for STS3215 firmware 3.10")
                if any(bus.read("Torque_Enable", name, normalize=False) != 0 for name in bus.motors):
                    raise RuntimeError("Another motor is enabled; do not identify during another session")
            bus_index, motor_id = SELECTIONS[args.joint]
            bus = buses[bus_index]
            name = next(name for name, motor in bus.motors.items() if motor.id == motor_id)
            preflight(bus, name)
            observer = CameraObserver()
            result.update({"bus": bus_index, "motorId": motor_id, **probe(bus, name, observer.observe)})
            result["maximumObservedDeltaCounts"] = max((abs(sample["position"]-result["initial"]["position"])
                                                       for sample in result["samples"]), default=0)
    except BaseException as error:
        result.update({"state": "aborted", "error": str(error)})
    finally:
        # Never call upstream robot.connect/configure or broad disconnect torque
        # helpers; only the selected motor was ever eligible for torque.
        try:
            for bus in buses:
                if bus.is_connected:
                    bus.disconnect(disable_torque=False)
            if observer:
                result["cameraObservations"] = observer.finish(directory)
            result["evidenceDirectory"] = str(directory)
            result["rejectedReplies"] = reply_mismatches
            (directory / "result.json").write_text(json.dumps(result))
            json.dump(result, sys.stdout)
        finally:
            try:
                if owns_pid:
                    pid_path.unlink(missing_ok=True)
            finally:
                if lease:
                    lease.close()  # Hold ownership through cleanup and evidence.
    if result["state"] != "completed":
        sys.exit(1)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt("Operator/host stopped identification")))
    main()
