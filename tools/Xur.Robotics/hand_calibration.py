"""Phase acknowledgements around the pinned upstream torque-off calibration.

No goal position, torque-enable or wheel configuration commands are permitted.
The browser never supplies register values. Calibration is backed up before any
write, invalidated during setup, and published only after explicit range review.
"""
import builtins
import contextlib
from dataclasses import asdict
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import select
import shutil
import sys
import time
import uuid

JOINTS = ("shoulder_pan", "shoulder_lift", "elbow_flex", "wrist_flex", "wrist_roll", "gripper")


def run(configuration):
    from bridge import STATE
    from lerobot_robot_xlerobot import XLerobot, XLerobotConfig
    if configuration["motionEnabled"]:
        raise ValueError("Disable motion before hand-guided calibration")
    robot = XLerobot(XLerobotConfig(id=configuration["robotId"], calibration_dir=STATE / "calibration",
                                   port1="/dev/arm_left", port2="/dev/arm_right", cameras={}))
    output = sys.stdout
    backup = STATE / ".build/evidence/hand-calibration" / uuid.uuid4().hex
    backup.mkdir(parents=True, mode=0o700)
    snapshots, originals = {}, {}
    old_input = builtins.input
    phase = "left-reference"
    recorded = {}
    mutated = False
    committed = False

    def check():
        # Both buses, including head and wheels, must remain de-energized.
        for bus in (robot.bus1, robot.bus2):
            torques = bus.sync_read("Torque_Enable", normalize=False)
            temperatures = bus.sync_read("Present_Temperature", normalize=False)
            if set(torques) != set(bus.motors) or any(int(v) != 0 for v in torques.values()):
                raise RuntimeError("Torque is not off on every motor. Disconnect motor power.")
            if set(temperatures) != set(bus.motors) or any(not 0 <= int(v) < 45 for v in temperatures.values()):
                raise RuntimeError("Temperature is missing or at least 45°C. Stop setup and inspect motor power.")

    def feedback(bus=None, names=None, mins=None, maxes=None, can_advance=True, detail=None):
        check()
        motors = []
        if bus is not None:
            positions = bus.sync_read("Present_Position", names, normalize=False)
            temperatures = bus.sync_read("Present_Temperature", names, normalize=False)
            for name in names:
                position = int(positions[name])
                if not 0 <= position <= 4095:
                    raise RuntimeError("Encoder crossed its single-turn boundary. Stop and choose a clear reference pose.")
                if mins is not None:
                    # A discontinuity at encoder wrap must not become a false
                    # almost-full-turn range in a hand-recorded calibration.
                    if abs(position - recorded.get(name, position)) > 512:
                        raise RuntimeError("Encoder discontinuity detected. Restart setup without crossing the encoder boundary.")
                    recorded[name] = position
                    mins[name] = min(mins[name], position)
                    maxes[name] = max(maxes[name], position)
                motors.append(dict(name=name, position=position, minimum=int(mins[name]) if mins is not None else position,
                                   maximum=int(maxes[name]) if maxes is not None else position,
                                   temperature=int(temperatures[name]), torque=0))
        elif phase in ("review", "completed"):
            for name, calibration in robot.calibration.items():
                if "wheel" in name:
                    continue
                bus = robot.bus1 if name in robot.bus1.motors else robot.bus2
                motors.append(dict(name=name, position=int(bus.read("Present_Position", name, normalize=False)),
                                   minimum=calibration.range_min, maximum=calibration.range_max,
                                   temperature=int(bus.read("Present_Temperature", name, normalize=False)), torque=0))
        if mins is not None:
            can_advance = all(maxes[name] - mins[name] >= 32 for name in names)
        print(json.dumps(dict(phase=phase, detail=detail or "Support the arm. Move only by hand, within clear travel.",
                              canAdvance=can_advance, torqueOffVerified=True, motors=motors)), file=output, flush=True)

    def command():
        if not select.select([sys.stdin], [], [], 0)[0]:
            return False
        value = sys.stdin.readline()
        if not value:
            raise RuntimeError("Setup connection closed. Calibration was not approved.")
        if value.strip() != phase:
            raise ValueError("Setup step changed; refresh before continuing")
        return True

    def reference(prompt=""):
        nonlocal phase
        # Exactly two reference prompts are expected from the pinned plugin.
        if "left arm and head" in prompt:
            phase, bus, names = "left-reference", robot.bus1, robot.left_arm_motors + robot.head_motors
        elif "right arm" in prompt:
            phase, bus, names = "right-reference", robot.bus2, robot.right_arm_motors
        else:
            raise RuntimeError("Unexpected upstream calibration prompt")
        while True:
            feedback(bus, names, detail="Place these joints at the middle of their clear travel. Keep the gripper half open. Support the arm before confirming.")
            if command():
                return ""
            time.sleep(.2)

    def ranges(bus, names, display_values=True):
        nonlocal phase
        phase = "left-range" if bus is robot.bus1 else "right-range"
        mins = {name: int(value) for name, value in bus.sync_read("Present_Position", names, normalize=False).items()}
        maxes = mins.copy()
        while True:
            feedback(bus, names, mins, maxes, detail="Move every listed joint one at a time through its clear travel. Avoid hard stops, pinches and encoder wrap. Then finish this side.")
            if command():
                if any(maxes[name] - mins[name] < 32 for name in names):
                    raise ValueError("Every joint needs at least 32 observed counts; a small observed span is not a usable range")
                return mins, maxes
            time.sleep(.2)

    try:
        with contextlib.redirect_stdout(sys.stderr):
            for bus in (robot.bus1, robot.bus2):
                bus.connect()
                bus.disable_torque()
            check()
            for index, bus in enumerate((robot.bus1, robot.bus2)):
                snapshots[index] = bus.read_calibration()
                originals[index] = dict(write=bus.write, write_calibration=bus.write_calibration,
                                        ranges=bus.record_ranges_of_motion,
                                        modes={name: int(bus.read("Operating_Mode", name, normalize=False)) for name in bus.motors})
            (backup / "registers.json").write_text(json.dumps({str(i): {"calibration": {n: asdict(v) for n, v in c.items()},
                "modes": originals[i]["modes"]} for i, c in snapshots.items()}, indent=2))
            calibration_dir = STATE / "calibration"
            calibration_dir.mkdir(exist_ok=True)
            for name in (configuration["robotId"] + ".json", configuration["robotId"] + "-left.json",
                         configuration["robotId"] + "-right.json", "receipt.json"):
                source = calibration_dir / name
                if source.exists():
                    shutil.copy2(source, backup / name)
            # Old approval is invalid while encoder offsets are being changed.
            (calibration_dir / "receipt.json").unlink(missing_ok=True)
            mutated = True
            for index, bus in enumerate((robot.bus1, robot.bus2)):
                original_write = originals[index]["write"]

                def guarded_write(register, motor, value, *args, _bus=bus, _write=original_write, **kwargs):
                    if register not in ("Torque_Enable", "Lock", "Operating_Mode", "Homing_Offset", "Min_Position_Limit", "Max_Position_Limit"):
                        raise RuntimeError("Unexpected register write during torque-off calibration")
                    if register == "Torque_Enable" and value != 0:
                        raise RuntimeError("Torque enable is prohibited during hand-guided setup")
                    if register == "Lock" and value != 0:
                        raise RuntimeError("Unexpected EEPROM lock command")
                    if "wheel" in str(motor) and register not in ("Torque_Enable", "Lock"):
                        raise RuntimeError("Wheel configuration is prohibited during arm calibration")
                    if register not in ("Torque_Enable", "Lock"):
                        check()
                    return _write(register, motor, value, *args, **kwargs)

                bus.write = guarded_write
                # Defer the final ranges until both sides and review complete.
                bus.write_calibration = lambda values, cache=True, _bus=bus: setattr(_bus, "calibration", values)
                bus.record_ranges_of_motion = lambda names, display_values=True, _bus=bus: ranges(_bus, names, display_values)
            builtins.input = reference
            robot._save_calibration = lambda: None
            robot.calibration = {}
            robot.calibrate()
            # Preserve wheel calibration exactly; no wheel range is invented.
            for name, value in snapshots[1].items():
                if "wheel" in name:
                    robot.calibration[name] = value
            phase = "review"
            while True:
                feedback(detail="Review every recorded range and confirm it covers clear travel. Save writes these measured ranges; motion stays disabled.")
                if command():
                    break
                time.sleep(.2)
            check()
            for index, bus in enumerate((robot.bus1, robot.bus2)):
                values = {name: value for name, value in robot.calibration.items() if name in bus.motors and "wheel" not in name}
                originals[index]["write_calibration"](values, cache=False)
                actual = bus.read_calibration()
                if any(asdict(actual[name]) != asdict(value) for name, value in values.items()):
                    raise RuntimeError("Written calibration failed register readback")
            check()
            files = {}
            content = {configuration["robotId"] + ".json": {name: asdict(value) for name, value in robot.calibration.items()}}
            for side in ("left", "right"):
                content[configuration["robotId"] + "-" + side + ".json"] = {joint: asdict(robot.calibration[f"{side}_arm_{joint}"]) for joint in JOINTS}
            for name, data in content.items():
                path = calibration_dir / name
                temporary = path.with_suffix(".json.tmp")
                temporary.write_text(json.dumps(data, indent=2))
                temporary.replace(path)
                files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
            receipt = {key: configuration[key] for key in ("robotId", "leftPort", "rightPort")}
            receipt.update(files=files, method="hand-guided", capturedAt=datetime.now(timezone.utc).isoformat(), backup=str(backup.relative_to(STATE)))
            temporary = calibration_dir / "receipt.json.tmp"
            temporary.write_text(json.dumps(receipt))
            temporary.replace(calibration_dir / "receipt.json")
            committed = True
            phase = "completed"
            feedback(detail="Calibration saved and verified. Motion is still disabled. Establish motor limits before a supervised movement check.", can_advance=False)
    finally:
        builtins.input = old_input
        failures = []
        with contextlib.redirect_stdout(sys.stderr):
            for index, bus in enumerate((robot.bus1, robot.bus2)):
                if not bus.is_connected:
                    continue
                try:
                    bus.disable_torque()
                    if mutated and not committed and index in snapshots:
                        bus.write = originals[index]["write"]
                        originals[index]["write_calibration"]({n: v for n, v in snapshots[index].items() if "wheel" not in n}, cache=False)
                        for name, mode in originals[index]["modes"].items():
                            if "wheel" not in name:
                                bus.write("Operating_Mode", name, mode, normalize=False)
                        actual = bus.read_calibration()
                        if any(asdict(actual[n]) != asdict(v) for n, v in snapshots[index].items()):
                            raise RuntimeError("Calibration restore did not verify")
                    if any(int(v) != 0 for v in bus.sync_read("Torque_Enable", normalize=False).values()):
                        raise RuntimeError("Torque-off cleanup failed")
                except Exception as error:
                    failures.append(str(error))
                finally:
                    try:
                        bus.disconnect(disable_torque=True)
                    except Exception as error:
                        failures.append(str(error))
        if failures:
            (STATE / "calibration/receipt.json").unlink(missing_ok=True)
            raise RuntimeError("Disconnect motor power: setup cleanup could not be verified: " + "; ".join(failures))
