"""Private, container-only adapter for reviewed LeRobot/XLeRobot operations.

The public .NET API sends task names. This adapter never accepts motor targets.
Calibration uses XLeRobot's hand-guided routine with torque disabled; it does
not discover hard stops by driving motors. The mobile base is always stationary.
"""
import base64
import contextlib
from datetime import datetime, timezone
from dataclasses import asdict, is_dataclass
import fcntl
import importlib.metadata
import importlib.util
import hashlib
import json
import math
import os
import runpy
from pathlib import Path
import signal
import struct
import subprocess
import sys
import tempfile
import time

STATE = Path("/state")
PID = STATE / ".build/adapter.json"
JOINTS = ("shoulder_pan", "shoulder_lift", "elbow_flex", "wrist_flex", "wrist_roll", "gripper")
ZERO_BASE = {"x.vel": 0.0, "y.vel": 0.0, "theta.vel": 0.0}
DEVICE_KEYS = ("leftPort", "rightPort", "controllerDevice", "headCamera", "handCamera")


def prepared(configuration):
    mapped = json.loads((STATE / ".build/devices.json").read_text())
    if any(mapped.get(key) != configuration[key] for key in DEVICE_KEYS):
        raise ValueError("Device selection changed; prepare the tools container again")


def bounded_action(action, observation, values):
    """Enforce the hand-recorded range as well as the upstream relative cap."""
    result = {}
    for key, target in action.items():
        if not key.endswith(".pos"):
            continue
        name = key.removesuffix(".pos")
        if name not in values or not math.isfinite(target) or not math.isfinite(observation[key]):
            raise ValueError("Unexpected or non-finite policy action")
        if "gripper" in name:
            low, high = 0.0, 100.0
        else:
            value = asdict(values[name]) if is_dataclass(values[name]) else values[name]
            half = (value["range_max"] - value["range_min"]) * 180 / 4095
            low, high = -half, half
        if observation[key] < low - 0.5 or observation[key] > high + 0.5:
            raise ValueError("Joint is outside its hand-recorded range; inspect and recalibrate")
        result[key] = max(low + 0.5, min(high - 0.5, target))
    return result


def temperature(bus, motors=None):
    values = bus.sync_read("Present_Temperature", motors, normalize=False)
    if any(value >= 55 for value in values.values()):
        raise RuntimeError("A motor reached the conservative temperature limit; stop and allow it to cool")



def motor_limits(configuration):
    limits = configuration.get("motorLimits")
    if not limits or not 1 <= limits["maxLoadRaw"] <= 1023 or not 1 <= limits["maxCurrentRaw"] <= 65535:
        raise ValueError("Establish robot-specific load/current limits before motion")
    error = limits["maxFollowingErrorDegrees"]
    if not math.isfinite(error) or not 0.1 <= error <= 3:
        raise ValueError("Establish a conservative joint following-error limit")
    return limits


def read_motor_limits():
    return motor_limits(json.loads((STATE / "config.json").read_text()))


def resistance(bus, limits, motors=None):
    expected = set(bus.motors if motors is None else motors)
    for register, limit in (("Present_Load", limits["maxLoadRaw"]),
                            ("Present_Current", limits["maxCurrentRaw"]),
                            ("Present_Temperature", 55)):
        values = bus.sync_read(register, motors, normalize=False)
        if set(values) != expected or not values or any(not math.isfinite(value) for value in values.values()):
            raise RuntimeError("Motor effort telemetry is incomplete or invalid")
        if any(abs(value) >= limit for value in values.values()):
            raise RuntimeError(f"Motor {register} reached its limit; stop and inspect resistance")


def following_error(previous, observed, limits):
    for key, target in previous.items():
        # Gripper observations use percent-of-range, not joint degrees. Its
        # resistance is still checked through load/current on every update.
        if key.endswith(".pos") and "gripper" not in key:
            actual = observed.get(key)
            if actual is None or not math.isfinite(actual) or abs(target - actual) > limits["maxFollowingErrorDegrees"]:
                raise RuntimeError("Joint did not follow its previous goal; stop and inspect resistance")


def validate_feetech_replies(bus, mismatches=None):
    """Reject wrong-sized read/write replies before the SDK decodes telemetry.

    feetech-servo-sdk 1.0.0 checks ID/checksum but does not compare reply payload
    length with the request. A delayed two-byte reply must not become a valid
    one-byte temperature. Keep the SDK's normal communication error/retry path.
    """
    handler = bus.packet_handler
    if getattr(handler, "xur_reply_lengths_checked", False):
        return
    from scservo_sdk import COMM_SUCCESS, COMM_RX_CORRUPT, INST_READ, INST_WRITE
    original = handler.txRxPacket
    def checked(port, request):
        reply, result, error = original(port, request)
        if result == COMM_SUCCESS and request[4] in (INST_READ, INST_WRITE):
            expected = request[6] + 2 if request[4] == INST_READ else 2
            if reply is None or len(reply) < 6 or reply[3] != expected or len(reply) != expected + 4:
                detail = {"instruction": request[4], "motorId": request[2],
                          "register": request[5], "expectedPacketLength": expected+4,
                          "reply": reply}
                if mismatches is not None:
                    mismatches.append(detail)
                print("Rejected wrong-sized Feetech reply: " + json.dumps(detail), file=sys.stderr)
                return reply, COMM_RX_CORRUPT, error
        return reply, result, error
    handler.txRxPacket = checked
    handler.xur_reply_lengths_checked = True


@contextlib.contextmanager
def zero_output_seed(*buses):
    """Position writes can auto-enable STS3215 torque; seed with zero output.

    Callers defer upstream enable helpers until this context has disabled every
    motor, restored runtime caps and verified the final torque-off state.
    """
    saved = []
    def disabled(bus):
        bus.disable_torque()
        if any(bus.read("Torque_Enable", name, normalize=False) != 0 for name in bus.motors):
            raise RuntimeError("Could not verify torque off during measured-pose seeding")
    def zeroed():
        # Upstream configuration can change protection/cap registers. Reassert
        # zero immediately before writing goals, after configuration finishes.
        for bus in buses:
            disabled(bus)
            for name in bus.motors:
                bus.write("Torque_Limit", name, 0, normalize=False)
                if bus.read("Torque_Limit", name, normalize=False) != 0:
                    raise RuntimeError("Zero drive output did not read back; do not seed goals")
    try:
        for bus in buses:
            disabled(bus)
            for name in bus.motors:
                saved.append((bus, name, bus.read("Torque_Limit", name, normalize=False)))
                bus.write("Torque_Limit", name, 0, normalize=False)
                if bus.read("Torque_Limit", name, normalize=False) != 0:
                    raise RuntimeError("Zero drive output did not read back; do not seed goals")
        yield zeroed
    finally:
        errors = []
        for bus in buses:
            try:
                disabled(bus)
            except BaseException as error:
                errors.append(str(error))
        # Keep zero caps if any bus cannot be verified off. No goal writes are
        # allowed here; restoration must never auto-enable a seeded motor.
        if not errors:
            for bus, name, value in saved:
                try:
                    bus.write("Torque_Limit", name, value, normalize=False)
                    if bus.read("Torque_Limit", name, normalize=False) != value:
                        raise RuntimeError("Runtime drive-output cap restoration failed")
                except BaseException as error:
                    errors.append(str(error))
        for bus in buses:
            try:
                disabled(bus)
            except BaseException as error:
                errors.append(str(error))
        if errors:
            raise RuntimeError("Motor startup cleanup failed; cut motor power: " + "; ".join(errors))


def local_path(value, category):
    path = Path(value).resolve()
    if not path.is_relative_to(STATE / category) or not path.exists():
        raise ValueError(f"Select an existing local {category} asset")
    return path


def calibration(configuration):
    robot_id = configuration["robotId"]
    receipt = json.loads((STATE / "calibration/receipt.json").read_text())
    if any(receipt[key] != configuration[key] for key in ("robotId", "leftPort", "rightPort")):
        raise ValueError("Calibration belongs to a different robot or bus assignment")
    result = {}
    for name in (robot_id, robot_id + "-left", robot_id + "-right"):
        path = STATE / "calibration" / (name + ".json")
        if receipt["files"][path.name] != hashlib.sha256(path.read_bytes()).hexdigest():
            raise ValueError("Calibration changed after it was checked")
        values = json.loads(path.read_text())
        expected = list(JOINTS) if name != robot_id else [
            *(f"{side}_arm_{joint}" for side in ("left", "right") for joint in JOINTS),
            "head_motor_1", "head_motor_2", "base_left_wheel", "base_back_wheel", "base_right_wheel",
        ]
        if set(values) != set(expected):
            raise ValueError(f"Calibration motor names differ for {name}")
        ids = {joint: index + 1 for index, joint in enumerate(JOINTS)}
        ids.update({f"{side}_arm_{joint}": index + 1 for side in ("left", "right") for index, joint in enumerate(JOINTS)})
        ids.update({"head_motor_1": 7, "head_motor_2": 8, "base_left_wheel": 7, "base_back_wheel": 8, "base_right_wheel": 9})
        for motor, value in values.items():
            if not 0 <= value["range_min"] < value["range_max"] <= 4095 or value["range_max"] - value["range_min"] < 32:
                raise ValueError(f"Invalid recorded range for {motor}")
            if value["id"] != ids[motor] or value["drive_mode"] != 0 or not -4095 <= value["homing_offset"] <= 4095:
                raise ValueError(f"Calibration identity or offset differs for {motor}")
        result[name] = values
    return result


class Xbox:
    """Read a specific evdev node, including xone/xpad wireless receivers.

    Kernel state ioctls avoid assuming SDL enumeration order, trigger rest
    values, or a particular driver's axis ranges. No new Python input library.
    """
    CODES = (0x130, 0x131, 0x134, 0x133, 0x136, 0x137, 0x13a, 0x13b, 0x13c, 0x13d, 0x13e)

    def __init__(self, path="/dev/xbox", grab=False):
        self.fd = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC)
        self.grab = grab
        try:
            self.axes = [self.axis(code) for code in range(6)]
            if grab:
                fcntl.ioctl(self.fd, 0x40044590, 1)  # EVIOCGRAB
        except BaseException:
            os.close(self.fd)
            raise

    def close(self):
        if self.grab:
            fcntl.ioctl(self.fd, 0x40044590, 0)
        os.close(self.fd)

    def axis(self, code):
        data = bytearray(24)
        fcntl.ioctl(self.fd, 0x80184540 + code, data)
        return struct.unpack("6i", data)  # value, min, max, fuzz, flat, resolution

    def update(self):
        # A disconnected or replaced receiver must raise, not retain held input.
        data = bytearray(96)
        fcntl.ioctl(self.fd, 0x80604518, data)  # EVIOCGKEY
        self.buttons = [bool(data[code // 8] & (1 << (code % 8))) for code in self.CODES]
        self.axes = [self.axis(code) for code in range(6)]

    def get_axis(self, index):
        value, low, high, _, flat, _ = self.axes[index]
        if high <= low:
            raise ValueError("Controller reports an invalid axis range")
        normalized = (value - low) / (high - low) * 2 - 1
        if index not in (2, 5) and abs(value - (low + high) / 2) <= max(flat, (high - low) * 0.12):
            return 0.0
        return max(-1.0, min(1.0, normalized))

    def get_button(self, index):
        return self.buttons[index]

    def get_numaxes(self):
        return 6

    def get_numbuttons(self):
        return len(self.CODES)

    def get_numhats(self):
        return 0  # The base/D-pad has no motion binding.


@contextlib.contextmanager
def opened_camera(name, full_resolution=False):
    import cv2
    if name not in ("head", "hand"):
        raise ValueError("Select head or hand")
    source = "/dev/camera_" + name
    capture = cv2.VideoCapture(source, cv2.CAP_V4L2)
    try:
        if not capture.isOpened():
            raise RuntimeError("Camera is disconnected or unavailable")
        if full_resolution:
            capture.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
        capture.set(cv2.CAP_PROP_FRAME_WIDTH, 1920 if full_resolution else 640)
        capture.set(cv2.CAP_PROP_FRAME_HEIGHT, 1080 if full_resolution else 480)
        capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        # Drain initial frames while auto-exposure settles. The outer .NET
        # operation deadline also bounds a disconnected driver's blocking read.
        settling = time.monotonic() + 1.0
        for _ in range(90):
            success, _ = capture.read()
            if not success:
                raise RuntimeError("Camera did not return a frame")
            if time.monotonic() >= settling:
                break
        yield capture
    finally:
        capture.release()


def encoded_camera_frame(frame):
    import cv2
    success, encoded = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 85])
    if not success or len(encoded) > 2 * 1024 * 1024:
        raise RuntimeError("Camera frame encoding failed or exceeded its limit")
    return base64.b64encode(encoded).decode("ascii")


def camera(configuration, request):
    with opened_camera(request["camera"]) as capture:
        success, frame = capture.read()
        if not success:
            raise RuntimeError("Camera did not return a frame")
        return {"jpeg": encoded_camera_frame(frame), "camera": request["camera"]}


def detect_frame(frame):
    import cv2
    height, width = frame.shape[:2]
    if not 16 <= width <= 3840 or not 16 <= height <= 2160:
        raise RuntimeError("Camera returned an unsupported frame size")
    success, encoded = cv2.imencode(".pgm", cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY))
    if not success:
        raise RuntimeError("Grayscale camera encoding failed")
    with tempfile.NamedTemporaryFile(dir=STATE / ".build", suffix=".pgm") as source:
        source.write(encoded.tobytes())
        source.flush()
        result = subprocess.run(["/opt/xur/detect-markers", source.name],
                                capture_output=True, check=True, timeout=10)
    if len(result.stdout) > 512 * 1024:
        raise RuntimeError("Marker detector response exceeded its limit")
    data = json.loads(result.stdout)
    if data["family"] != "tagStandard41h12" or data["width"] != width or data["height"] != height:
        raise RuntimeError("Marker detector returned mismatched image geometry")
    if len(data["detections"]) > 128 or any(marker["hamming"] != 0 for marker in data["detections"]):
        raise RuntimeError("Marker detector returned unexpected detections")
    return {"width": width, "height": height, "detections": data["detections"]}


def inspect_markers(configuration, request):
    # No controller, serial bus, robot connection, calibration or torque calls.
    # Full resolution is requested, and the driver's actual dimensions are saved.
    cameras = []
    for name in ("head", "hand"):
        frames = []
        with opened_camera(name, full_resolution=True) as capture:
            for _ in range(3):
                success, frame = capture.read()
                captured_at = datetime.now(timezone.utc).isoformat()
                if not success:
                    raise RuntimeError("Camera did not return a marker-survey frame")
                frames.append({"capturedAt": captured_at, **detect_frame(frame)})
            cameras.append({"name": name, "frames": frames, "jpeg": encoded_camera_frame(frame)})
    return {"family": "tagStandard41h12", "cameras": cameras,
            "detectorSha256": hashlib.sha256(Path("/opt/xur/detect-markers").read_bytes()).hexdigest()}


def probe(configuration, request):
    result = {"lerobot": importlib.metadata.version("lerobot"), "baseMotion": False}
    if configuration.get("controllerDevice"):
        xbox = Xbox()
        try:
            xbox.update()
            result["controller"] = {"axes": xbox.axes, "buttons": xbox.buttons}
        finally:
            xbox.close()
    else:
        result["controller"] = None
    try:
        calibration(configuration)
        result["calibrated"] = True
    except (OSError, ValueError, KeyError):
        result["calibrated"] = False
    return result


MOTOR_REGISTERS = (
    "Model_Number", "Firmware_Major_Version", "Firmware_Minor_Version", "ID",
    "Present_Position", "Present_Velocity", "Present_Load", "Present_Current",
    "Present_Temperature", "Present_Voltage", "Torque_Enable", "Moving", "Status",
    "Operating_Mode", "Homing_Offset", "Min_Position_Limit", "Max_Position_Limit",
    "Min_Voltage_Limit", "Max_Voltage_Limit", "Max_Temperature_Limit", "Max_Torque_Limit", "Torque_Limit",
    "Protection_Current", "Protective_Torque", "Protection_Time", "Overload_Torque",
    "Acceleration", "Goal_Position", "Goal_Velocity")


def motor_details(configuration, bus_factory=None, motor_factory=None):
    """Read all discovered motors, including wheels, without any register writes."""
    if bus_factory is None:
        from lerobot.motors import Motor, MotorNormMode
        from lerobot.motors.feetech import FeetechMotorsBus
        bus_factory = FeetechMotorsBus
        motor_factory = lambda motor_id: Motor(motor_id, "sts3215", MotorNormMode.DEGREES)
    reports = []
    for role, count, extra in (("left/head", 8, ("head_pan", "head_tilt")),
                               ("right/wheels", 9, ("left_wheel", "back_wheel", "right_wheel"))):
        side = role.split("/")[0]
        report = {"role": role, "port": configuration[side + "Port"], "motors": []}
        # LeRobot caches ID/name/model lookups in the bus constructor.
        # Supply the complete expected map before connecting; ping below must
        # still verify this inventory before any register details are read.
        motors = {str(motor_id): motor_factory(motor_id) for motor_id in range(1, count + 1)}
        bus = bus_factory("/dev/arm_" + side, motors)
        try:
            bus.connect(handshake=False)
            bus.set_baudrate(1_000_000)
            if hasattr(bus, "packet_handler"):
                validate_feetech_replies(bus)
            models = bus.broadcast_ping(raise_on_error=True)
            if models != {motor_id: 777 for motor_id in range(1, count + 1)}:
                raise ValueError("Inventory does not match this upstream XLeRobot bus; do not guess joint roles")
            names = [side + "_" + joint for joint in JOINTS] + list(extra)
            for motor_id in sorted(models):
                item = {"id": motor_id, "name": names[motor_id - 1], "model": "STS3215", "registers": {}}
                try:
                    for register in MOTOR_REGISTERS:
                        item["registers"][register] = bus.read(register, str(motor_id), normalize=False)
                except Exception as error:
                    item["error"] = str(error)
                item["observedAt"] = datetime.now(timezone.utc).isoformat()
                report["motors"].append(item)
        except Exception as error:
            report["error"] = str(error)
        finally:
            if bus.is_connected:
                bus.disconnect(disable_torque=False)
        reports.append(report)
    return reports


def dashboard(configuration, request):
    result = {"observedAt": datetime.now(timezone.utc).isoformat(),
              "buses": motor_details(configuration), "cameras": [], "motorCommandsIssued": False}
    for name in ("head", "hand"):
        try:
            image = camera(configuration, {"camera": name})
            result["cameras"].append({"name": name, "capturedAt": datetime.now(timezone.utc).isoformat(), "jpeg": image["jpeg"]})
        except Exception as error:
            result["cameras"].append({"name": name, "capturedAt": datetime.now(timezone.utc).isoformat(), "error": str(error)})
    return result


def seed_arm(arm, observation):
    arm.target_positions = {joint: observation[f"{arm.prefix}_arm_{joint}.pos"] for joint in JOINTS}
    arm.current_x, arm.current_y = arm.kinematics.forward_kinematics(
        arm.target_positions["shoulder_lift"], arm.target_positions["elbow_flex"])
    arm.pitch = sum(arm.target_positions[joint] for joint in ("shoulder_lift", "elbow_flex", "wrist_flex"))
    arm.xy_step = 0.0005
    arm.degree_step = 0.25


def connect_robot(configuration, recording=False):
    limits = motor_limits(configuration)
    from lerobot_robot_xlerobot import XLerobot, XLerobotConfig
    from lerobot.cameras.opencv.configuration_opencv import OpenCVCameraConfig

    class StationaryRobot(XLerobot):
        def configure(self):
            if not self.is_calibrated:
                raise ValueError("Motor calibration differs; recalibrate with an operator")
            enables = (self.bus1.enable_torque, self.bus2.enable_torque)
            self.bus1.enable_torque = self.bus2.enable_torque = lambda *args, **kwargs: None
            try:
                with zero_output_seed(self.bus1, self.bus2) as zeroed:
                    super().configure()
                    zeroed()
                    observed = self.get_observation()
                    self.send_action({**{key: value for key, value in observed.items() if key.endswith(".pos")}, **ZERO_BASE})
            finally:
                self.bus1.enable_torque, self.bus2.enable_torque = enables
            enables[0]()
            enables[1](self.right_arm_motors)  # Wheel torque stays disabled.

        def send_action(self, action):
            start = time.monotonic()
            if any(action.get(key, 0) != 0 for key in ZERO_BASE):
                raise ValueError("Base movement is disabled")
            from lerobot.robots.utils import ensure_safe_goal_position
            observed = self.get_observation()
            limited = bounded_action(action, observed, self.calibration)
            following_error(getattr(self, "xur_previous", {}), observed, limits)
            resistance(self.bus1, limits)
            resistance(self.bus2, limits, self.right_arm_motors)
            if time.monotonic() - start > 0.5:
                raise RuntimeError("Robot observation stalled")
            safe = ensure_safe_goal_position({key: (value, observed[key]) for key, value in limited.items()}, 1)
            result = super().send_action({**safe, **ZERO_BASE})
            self.xur_previous = {key: value for key, value in result.items() if key.endswith(".pos")}
            return result

    cameras = {}
    if recording:
        cameras = {name: OpenCVCameraConfig(index_or_path=f"/dev/camera_{name}", fps=10, width=640, height=480)
                   for name in ("head", "hand")}
    robot = StationaryRobot(XLerobotConfig(
        id=configuration["robotId"], calibration_dir=STATE / "calibration",
        port1="/dev/arm_left", port2="/dev/arm_right", use_degrees=True,
        # The pinned plugin mismatches suffixed action and observation keys in
        # its clamp. The override above uses LeRobot's helper with matching keys.
        max_relative_target=None, cameras=cameras))
    validate_feetech_replies(robot.bus1)
    validate_feetech_replies(robot.bus2)
    if not robot.calibration_fpath.is_file():
        raise ValueError("Calibrate before connecting the robot")
    # The pinned plugin asks whether to restore its existing calibration. It
    # must never start interactive calibration from an API motion operation.
    import builtins
    previous = builtins.input

    def restore(prompt):
        if "restore calibration from file" not in prompt:
            raise RuntimeError("Unexpected calibration prompt; stop and recalibrate with an operator")
        return ""

    builtins.input = restore
    try:
        robot.connect(calibrate=False)
        if not robot.is_calibrated:
            raise ValueError("Motor calibration differs from the recorded calibration")
    except BaseException:
        disconnect(robot)
        raise
    finally:
        builtins.input = previous
    return robot


def controller(configuration, request, recording=False):
    calibration(configuration)
    import numpy as np
    from xlerobot_model.SO101Robot import SO101Kinematics
    script = Path("/opt/XLeRobot/software/examples/5_xlerobot_teleop_xbox.py")
    spec = importlib.util.spec_from_file_location("xur_upstream_xbox", script)
    upstream = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(upstream)  # Import helpers; never call upstream main().
    xbox = Xbox(grab=True)
    robot = None
    dataset = None
    try:
        xbox.update()
        if xbox.get_button(7) or xbox.get_button(6):
            raise ValueError("Release Start and Back before starting a session")
        robot = connect_robot(configuration, recording)
        observed = robot.get_observation()
        arms = [upstream.SimpleTeleopArm(SO101Kinematics(), mapping, observed, prefix=side)
                for side, mapping in (("left", upstream.LEFT_JOINT_MAP), ("right", upstream.RIGHT_JOINT_MAP))]
        for arm in arms:
            seed_arm(arm, observed)
        head = upstream.SimpleHeadControl(observed)
        head.degree_step = 0.25
        origin = {key: value for key, value in observed.items() if key.endswith(".pos")}
        if recording:
            from lerobot.datasets.lerobot_dataset import LeRobotDataset
            name = request["dataset"]
            path = STATE / "datasets" / name
            receipt = {"arm": request["arm"], "task": request["task"],
                "calibrationHash": hashlib.sha256((STATE / "calibration/receipt.json").read_bytes()).hexdigest(),
                "headCamera": configuration["headCamera"], "handCamera": configuration["handCamera"]}
            previous = path / "meta/xur-demonstration.json"
            names = [joint + ".pos" for joint in JOINTS]
            features = {
                "observation.state": {"dtype": "float32", "shape": (6,), "names": names},
                "action": {"dtype": "float32", "shape": (6,), "names": names},
                **{f"observation.images.{name}": {"dtype": "video", "shape": (480, 640, 3), "names": ["height", "width", "channels"]}
                   for name in ("head", "hand")},
            }
            if path.exists():
                if not previous.exists() or json.loads(previous.read_text()) != receipt:
                    raise ValueError("Dataset belongs to a different arm, task, cameras or calibration; use a new name")
                dataset = LeRobotDataset.resume(repo_id="xur/" + name, root=path, image_writer_threads=2)
            else:
                dataset = LeRobotDataset.create(repo_id="xur/" + name, fps=10, root=path,
                    robot_type="so101_follower", features=features, image_writer_threads=2)
                previous.write_text(json.dumps(receipt))
        end = time.monotonic() + request["seconds"]
        held_frames = 0
        while time.monotonic() < end:
            tick = time.monotonic()
            xbox.update()
            if xbox.get_button(6):
                raise KeyboardInterrupt("Back pressed")
            observed = robot.get_observation()
            action = {**{key: value for key, value in observed.items() if key.endswith(".pos")}, **ZERO_BASE}
            if xbox.get_button(7):  # Hold Start as the deadman switch.
                held_frames += 1
                for arm, mapping in zip(arms, (upstream.LEFT_KEYMAP, upstream.RIGHT_KEYMAP), strict=True):
                    if recording and arm.prefix != request["arm"]:
                        continue
                    keys = upstream.get_xbox_key_state(xbox, mapping)
                    previous_gripper = arm.target_positions["gripper"]
                    arm.handle_keys(keys)
                    # The upstream helper auto-opens idle grippers. Preserve
                    # grip on release; trigger closes, trigger+stick press opens.
                    trigger = 2 if arm.prefix == "left" else 5
                    stick = 9 if arm.prefix == "left" else 10
                    delta = (1 if xbox.get_button(stick) else -1) if xbox.get_axis(trigger) > 0.5 else 0
                    arm.target_positions["gripper"] = max(0, min(100, previous_gripper + delta))
                    action.update(arm.p_control_action(robot))
                if not recording:
                    head.handle_keys(upstream.get_xbox_key_state(xbox, upstream.LEFT_KEYMAP))
                    action.update(head.p_control_action(robot))
            else:
                for arm in arms:
                    seed_arm(arm, observed)
                head.target_positions = {motor: observed[motor + ".pos"] for motor in head.target_positions}
            # Initial controller preparation stays within 30 degrees of its
            # starting pose and 1 degree per observation at 10 Hz.
            for key, value in action.items():
                if not math.isfinite(value):
                    raise ValueError("Invalid upstream teleoperation target")
                if key.endswith(".pos") and "gripper" not in key:
                    action[key] = max(origin[key] - 30, min(origin[key] + 30, value))
            if time.monotonic() - tick > 0.5:
                raise RuntimeError("Control loop stalled; stop and inspect the robot")
            accepted = robot.send_action(action)
            if dataset is not None:
                prefix = request["arm"] + "_arm_"
                dataset.add_frame({
                    "observation.state": np.array([observed[prefix + j + ".pos"] for j in JOINTS], dtype=np.float32),
                    "action": np.array([accepted[prefix + j + ".pos"] for j in JOINTS], dtype=np.float32),
                    "observation.images.head": observed["head"], "observation.images.hand": observed["hand"], "task": request["task"],
                })
            time.sleep(max(0, 0.1 - (time.monotonic() - tick)))
        if dataset is not None:
            if held_frames < 10:
                raise ValueError("No usable demonstration: hold Start while demonstrating the task")
            dataset.save_episode()
        return {"sessionEnded": True, "baseMotion": False, "recorded": recording}
    finally:
        try:
            if robot is not None:
                disconnect(robot)
        finally:
            try:
                xbox.close()
            finally:
                if dataset is not None:
                    dataset.finalize()


def disconnect(robot):
    # A camera or one bus can disconnect independently. Release every other
    # bus even when the upstream aggregate is_connected flag becomes false.
    failures = []
    for bus in (getattr(robot, "bus1", None), getattr(robot, "bus2", None), getattr(robot, "bus", None)):
        try:
            if bus is not None and bus.is_connected:
                bus.disconnect(disable_torque=True)
        except Exception as error:
            failures.append(str(error))
    for cam in robot.cameras.values():
        try:
            if cam.is_connected:
                cam.disconnect()
        except Exception as error:
            failures.append(str(error))
    if failures:
        raise RuntimeError("Disconnect failed; use the physical motor-power stop: " + "; ".join(failures))


def run_cli(module, arguments):
    command = [sys.executable, "-m", module, *arguments]
    if module in ("lerobot.scripts.lerobot_replay", "lerobot.scripts.lerobot_rollout"):
        command = [sys.executable, "/opt/xur/bridge.py", "--upstream", module, *arguments]
    child = subprocess.Popen(command,
        stdin=subprocess.DEVNULL, stdout=sys.stderr, stderr=sys.stderr, start_new_session=True)
    try:
        if child.wait() != 0:
            raise RuntimeError("Upstream LeRobot tool failed; inspect its job log")
    except BaseException:
        if child.poll() is None:
            os.killpg(child.pid, signal.SIGINT)
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait()
        raise
    return {"toolFinished": True}


def guarded_upstream(module, arguments):
    if module not in ("lerobot.scripts.lerobot_replay", "lerobot.scripts.lerobot_rollout"):
        raise ValueError("Unsupported upstream motion tool")
    limits = read_motor_limits()
    from lerobot.robots.so_follower.so_follower import SOFollower
    original_configure, original_send, original_connect = SOFollower.configure, SOFollower.send_action, SOFollower.connect
    connected = []

    def configure(self):
        enable = self.bus.enable_torque
        # Its nested torque_disabled context re-enables torque on exit. Defer
        # that enable until both configuration and measured-pose seeding finish.
        self.bus.enable_torque = lambda *args, **kwargs: None
        try:
            if not self.is_calibrated:
                raise ValueError("Motor calibration differs; recalibrate with an operator")
            with zero_output_seed(self.bus) as zeroed:
                original_configure(self)
                zeroed()
                observed = self.get_observation()
                resistance(self.bus, limits)
                original_send(self, bounded_action(observed, observed, self.calibration))
                self.xur_previous = {key: value for key, value in observed.items() if key.endswith(".pos")}
        finally:
            self.bus.enable_torque = enable
        enable()

    def connect(self, calibrate=True):
        connected.append(self)
        try:
            validate_feetech_replies(self.bus)
            return original_connect(self, calibrate=False)
        except BaseException:
            disconnect(self)
            raise

    def send(self, action):
        start = time.monotonic()
        observed = self.get_observation()
        limited = bounded_action(action, observed, self.calibration)
        following_error(getattr(self, "xur_previous", {}), observed, limits)
        resistance(self.bus, limits)
        if time.monotonic() - start > 0.5:
            raise RuntimeError("Arm observation stalled")
        result = original_send(self, limited)
        self.xur_previous = result
        return result

    SOFollower.configure, SOFollower.send_action, SOFollower.connect = configure, send, connect
    sys.argv = [module, *arguments]
    try:
        runpy.run_module(module, run_name="__main__")
    finally:
        for robot in connected:
            disconnect(robot)


def skill(configuration, request, emote=False):
    calibration(configuration)
    selected = request["skill"]
    if not selected["verified"] or selected["arm"] not in ("left", "right"):
        raise ValueError("Select a verified single-arm skill")
    if selected["calibrationHash"] != hashlib.sha256((STATE / "calibration/receipt.json").read_bytes()).hexdigest():
        raise ValueError("Skill was reviewed under a different calibration")
    path = local_path(selected["assetPath"], "datasets" if emote else "policies")
    if emote:
        recording = json.loads((path / "meta/xur-demonstration.json").read_text())
    else:
        ancestors = [path, *path.parents]
        marker = next((p / "xur-policy.json" for p in ancestors if p.is_relative_to(STATE / "policies") and (p / "xur-policy.json").exists()), None)
        if marker is None:
            raise ValueError("Policy was not prepared from this robot's local demonstrations")
        recording = json.loads(marker.read_text())
    if recording["calibrationHash"] != selected["calibrationHash"] or recording["arm"] != selected["arm"]:
        raise ValueError("Skill asset belongs to another arm or calibration")
    if not emote and any(recording[key] != configuration[key] for key in ("headCamera", "handCamera")):
        raise ValueError("Sorting cameras differ from the training demonstrations")
    arguments = ["--robot.type=so101_follower", "--robot.port=/dev/arm_" + selected["arm"],
        "--robot.id=" + configuration["robotId"] + "-" + selected["arm"],
        "--robot.calibration_dir=/state/calibration", "--robot.use_degrees=true", "--robot.max_relative_target=1", "--play_sounds=false"]
    if emote:
        from lerobot.datasets.lerobot_dataset import LeRobotDataset
        info = json.loads((path / "meta/info.json").read_text())
        if info["features"]["action"]["names"] != [joint + ".pos" for joint in JOINTS]:
            raise ValueError("Emotes must contain only this single arm's actions")
        episode = LeRobotDataset("xur/" + path.name, root=path, episodes=[selected["episode"]])
        if episode.num_frames / 10 > selected["seconds"]:
            raise ValueError("Recorded emote exceeds the reviewed duration")
        arguments += ["--dataset.repo_id=" + info["repo_id"] if "repo_id" in info else "--dataset.repo_id=xur/" + path.name,
            "--dataset.root=" + str(path), "--dataset.episode=" + str(selected["episode"]), "--dataset.fps=10"]
        return run_cli("lerobot.scripts.lerobot_replay", arguments)
    cameras = {name: {"type": "opencv", "index_or_path": "/dev/camera_" + name, "fps": 10, "width": 640, "height": 480} for name in ("head", "hand")}
    arguments += ["--strategy.type=base", "--policy.path=" + str(path), "--device=cpu", "--fps=10",
        "--duration=" + str(selected["seconds"]), "--task=" + selected["task"],
        "--robot.cameras=" + json.dumps(cameras), "--return_to_initial_position=false"]
    return run_cli("lerobot.scripts.lerobot_rollout", arguments)


def train(configuration, request):
    path = local_path(str(STATE / "datasets" / request["dataset"]), "datasets")
    output = STATE / "policies" / request["policy"]
    if output.exists():
        raise ValueError("Use a new policy name; existing checkpoints are preserved")
    receipt = json.loads((path / "meta/xur-demonstration.json").read_text())
    result = run_cli("lerobot.scripts.lerobot_train", [
        "--dataset.repo_id=xur/" + request["dataset"], "--dataset.root=" + str(path),
        "--policy.type=act", "--policy.device=cpu", "--policy.push_to_hub=false",
        "--policy.pretrained_backbone_weights=null", "--batch_size=4", "--num_workers=0",
        "--steps=" + str(request["steps"]), "--save_freq=" + str(request["steps"]),
        "--env_eval_freq=0", "--wandb.enable=false", "--output_dir=" + str(output),
        "--job_name=" + request["policy"],
    ])
    (output / "xur-policy.json").write_text(json.dumps(receipt))
    return result


def calibrate(configuration):
    if configuration["motionEnabled"]:
        raise ValueError("Disable motion in Robotics setup before hand-guided calibration")
    from lerobot_robot_xlerobot import XLerobot, XLerobotConfig
    robot = XLerobot(XLerobotConfig(id=configuration["robotId"], calibration_dir=STATE / "calibration",
        port1="/dev/arm_left", port2="/dev/arm_right", cameras={}))
    print("Support both arms before continuing. Keep the base power isolated if possible.")
    input("Confirm you are beside the robot with arms supported, then press ENTER: ")
    # Avoid connect/configure: the plugin's configure enables torque. The
    # upstream calibrate method works directly with its connected motor buses.
    try:
        robot.bus1.connect()
        robot.bus2.connect()
        robot.bus1.disable_torque()
        robot.bus2.disable_torque()
        robot.calibration = {}  # Explicit recalibration, never auto-restore.
        robot.calibrate()
        for side in ("left", "right"):
            values = {joint: robot.calibration[f"{side}_arm_{joint}"] for joint in JOINTS}
            destination = STATE / "calibration" / (configuration["robotId"] + "-" + side + ".json")
            destination.write_text(json.dumps({joint: asdict(value) for joint, value in values.items()}, indent=2))
        files = {name + ".json": hashlib.sha256((STATE / "calibration" / (name + ".json")).read_bytes()).hexdigest()
                 for name in (configuration["robotId"], configuration["robotId"] + "-left", configuration["robotId"] + "-right")}
        receipt = {key: configuration[key] for key in ("robotId", "leftPort", "rightPort")}
        receipt["files"] = files
        (STATE / "calibration/receipt.json").write_text(json.dumps(receipt))
        calibration(configuration)
        print("Calibration saved and checked. Motion is still disabled; verify clearances before enabling it.")
    finally:
        disconnect(robot)


def stop():
    if not PID.exists():
        return
    identity = json.loads(PID.read_text())
    try:
        actual = Path(f"/proc/{identity['pid']}/stat").read_text().split(") ", 1)[1].split()[19]
        command = Path(f"/proc/{identity['pid']}/cmdline").read_bytes()
    except FileNotFoundError:
        return
    scripts = (b"/opt/xur/bridge.py", b"/opt/xur/motor_probe.py")
    if actual == identity["started"] and any(script in command.split(b"\0") for script in scripts):
        os.kill(identity["pid"], signal.SIGINT)


def main():
    if sys.argv[1:] == ["--stop"]:
        stop()
        return
    if len(sys.argv) >= 3 and sys.argv[1] == "--upstream":
        guarded_upstream(sys.argv[2], sys.argv[3:])
        return
    PID.parent.mkdir(parents=True, exist_ok=True)
    with (PID.parent / "adapter.lock").open("w") as lease:
        fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if sys.argv[1:] == ["--idle"]:
            return
        started = Path("/proc/self/stat").read_text().split(") ", 1)[1].split()[19]
        PID.write_text(json.dumps({"pid": os.getpid(), "started": started}))
        try:
            if sys.argv[1:] == ["--calibrate"]:
                configuration = json.loads((STATE / "config.json").read_text())
                prepared(configuration)
                calibrate(configuration)
                return
            data = json.load(sys.stdin)
            configuration, request = data["configuration"], data.get("request")
            prepared(configuration)
            operation = data["operation"]
            operations = {"probe": probe, "camera": camera, "dashboard": dashboard, "inspect-markers": inspect_markers, "controller": controller, "train": train,
                "sort": skill, "emote": lambda c, r: skill(c, r, True), "record": lambda c, r: controller(c, r, True)}
            if operation not in operations:
                raise ValueError("Unsupported robotics operation")
            if operation in ("controller", "sort", "emote", "record") and not configuration["motionEnabled"]:
                raise ValueError("Hardware motion is disabled")
            # Keep upstream prints out of the one JSON response on stdout.
            with contextlib.redirect_stdout(sys.stderr):
                result = operations[operation](configuration, request)
            json.dump(result, sys.stdout)
        finally:
            PID.unlink(missing_ok=True)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("Robot operation interrupted; inspect it before restarting.", file=sys.stderr)
        sys.exit(130)
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
