#!/usr/bin/env python3
"""Exercise adapter safety without installing robotics packages or opening hardware."""
import importlib.util
import contextlib
from dataclasses import make_dataclass
import json
import math
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

repo = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("robotics_bridge", repo / "tools/Xur.Robotics/bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


discovery_spec = importlib.util.spec_from_file_location("robotics_discovery", repo / "tools/Xur.Robotics/discover_buses.py")
discovery = importlib.util.module_from_spec(discovery_spec)
discovery_spec.loader.exec_module(discovery)


class DiscoverySafety(unittest.TestCase):
    def test_dashboard_reads_every_motor_without_writes_even_when_a_bus_fails(self):
        events = []
        class Bus:
            is_connected = False
            def __init__(self, port, motors): self.port = port; self.motors = motors
            def connect(self, handshake): self.is_connected = True; events.append(("connect", handshake))
            def set_baudrate(self, baud): events.append(("host-baud", baud))
            def broadcast_ping(self, raise_on_error):
                return {i: 777 for i in range(1, 9 if self.port.endswith('left') else 10)}
            def read(self, register, motor, normalize):
                events.append(("read", register, motor, normalize)); return 0
            def disconnect(self, disable_torque): events.append(("disconnect", disable_torque))
        configuration = {"leftPort": "/dev/serial/by-id/left", "rightPort": "/dev/serial/by-id/right"}
        reports = bridge.motor_details(configuration, Bus, lambda _: object())
        self.assertEqual([len(report['motors']) for report in reports], [8, 9])
        self.assertEqual(reports[0]['motors'][6]['name'], 'head_pan')
        self.assertEqual(reports[1]['motors'][8]['name'], 'right_wheel')
        self.assertEqual(len([item for item in events if item[0] == 'read']), 17 * len(bridge.MOTOR_REGISTERS))
        self.assertEqual([item for item in events if item[0] == 'disconnect'], [('disconnect', False)] * 2)
        class MissingBus(Bus):
            def broadcast_ping(self, **kwargs):
                if self.port.endswith('left'): raise OSError('Disconnected')
                return super().broadcast_ping(**kwargs)
        reports = bridge.motor_details(configuration, MissingBus, lambda _: object())
        self.assertEqual(reports[0]['error'], 'Disconnected')
        self.assertEqual(len(reports[1]['motors']), 9)

    def test_discovery_never_changes_torque_or_configures_motors(self):
        calls = []
        class Bus:
            is_connected = False
            def __init__(self, port, motors):
                self.assert_empty = motors == {}
                calls.append(("create", port, motors))
            def connect(self, handshake):
                calls.append(("connect", handshake)); self.is_connected = True
            def set_baudrate(self, baud):
                calls.append(("host-baud", baud))
            def broadcast_ping(self, raise_on_error):
                calls.append(("inventory", raise_on_error)); return {1: 777}
            def disconnect(self, disable_torque):
                calls.append(("disconnect", disable_torque)); self.is_connected = False
        self.assertEqual(discovery.inventory("/dev/candidate0", Bus), {1: 777})
        self.assertEqual(calls, [("create", "/dev/candidate0", {}), ("connect", False),
            ("host-baud", 1_000_000), ("inventory", True), ("disconnect", False)])

    def test_health_uses_only_reads_and_preserves_torque(self):
        calls = []
        class Bus:
            is_connected = False
            def __init__(self, port, motors): calls.append(("open", port, set(motors)))
            def connect(self, handshake): self.is_connected = True; calls.append(("connect", handshake))
            def set_baudrate(self, baud): pass
            def read(self, register, motor, normalize):
                calls.append(("read", register, motor, normalize)); return 0
            def disconnect(self, disable_torque): calls.append(("disconnect", disable_torque))
        result = discovery.health("/dev/candidate0", {1: 777}, Bus, lambda motor_id: object())
        self.assertEqual(len(result["readings"]), 1)
        self.assertEqual(result["readings"][0]["loadRaw"], 0)
        self.assertEqual(calls[1], ("connect", False))
        self.assertEqual(calls[-1], ("disconnect", False))
        self.assertEqual(len([c for c in calls if c[0] == "read"]), 7)
        with self.assertRaises(ValueError): discovery.health("/dev/candidate0", {1: 2825}, Bus, lambda _: object())

    def test_discovery_failure_closes_port_without_torque_write(self):
        closed = []
        class Bus:
            is_connected = True
            def __init__(self, *args): pass
            def connect(self, handshake): pass
            def set_baudrate(self, baud): pass
            def broadcast_ping(self, **kwargs): raise ConnectionError("no response")
            def disconnect(self, disable_torque): closed.append(disable_torque)
        with self.assertRaises(ConnectionError): discovery.inventory("/dev/candidate0", Bus)
        self.assertEqual(closed, [False])


class Safety(unittest.TestCase):
    def test_stop_targets_identification_and_bridge_but_rejects_lookalike_argv(self):
        identity = types.SimpleNamespace(exists=lambda:True,read_text=lambda:json.dumps({"pid":10,"started":"3"}))
        for script,allowed in ((b"/opt/xur/bridge.py",True),(b"/opt/xur/motor_probe.py",True),(b"/opt/xur/motor_probe.py-copy",False)):
            proc = types.SimpleNamespace(read_text=lambda:"10 (python) "+" ".join(["0"]*19+["3"]),read_bytes=lambda:b"python\0"+script+b"\0")
            with patch.object(bridge,"PID",identity),patch.object(bridge,"Path",return_value=proc),patch.object(bridge.os,"kill") as kill:
                bridge.stop()
            self.assertEqual(kill.call_count,int(allowed))

    def test_marker_inspection_uses_only_selected_cameras(self):
        from datetime import datetime, timezone
        calls = []
        class Camera:
            def read(self):
                calls.append("frame")
                return True, object()
        @contextlib.contextmanager
        def opened(name, full_resolution=False):
            calls.append(("camera", name, full_resolution))
            yield Camera()
        with patch.object(bridge, "opened_camera", opened), patch.object(bridge, "detect_frame", return_value={"width": 100, "height": 100, "detections": []}), \
                patch.object(bridge, "encoded_camera_frame", return_value="/9j/2Q=="), \
                patch.object(bridge.Path, "read_bytes", return_value=b"native detector"), \
                patch.object(bridge, "calibration", side_effect=AssertionError("Must not calibrate")), \
                patch.object(bridge, "connect_robot", side_effect=AssertionError("Must not connect motors")), \
                patch.object(bridge, "Xbox", side_effect=AssertionError("Must not inspect controller")):
            result = bridge.inspect_markers({"motionEnabled": False}, {})
        self.assertEqual([c["name"] for c in result["cameras"]], ["head", "hand"])
        self.assertEqual(calls.count("frame"), 6)
        self.assertIn(("camera", "head", True), calls)
        self.assertIn(("camera", "hand", True), calls)
        for camera in result["cameras"]:
            self.assertEqual(len(camera["frames"]), 3)
            self.assertTrue(all(datetime.fromisoformat(f["capturedAt"]).tzinfo == timezone.utc for f in camera["frames"]))

    def test_motion_requires_explicit_effort_limits(self):
        with self.assertRaises(ValueError): bridge.motor_limits({})

    def test_effort_threshold_or_missing_feedback_aborts(self):
        limits = {"maxLoadRaw": 100, "maxCurrentRaw": 100, "maxFollowingErrorDegrees": 2}
        class Bus:
            motors = {"joint": object()}
            def __init__(self, register=None): self.register = register
            def sync_read(self, register, motors, normalize):
                return {"joint": 100 if register == self.register else 0}
        bridge.resistance(Bus(), limits)
        for register in ["Present_Load", "Present_Current", "Present_Temperature"]:
            with self.assertRaises(RuntimeError): bridge.resistance(Bus(register), limits)
        bad = Bus(); bad.sync_read = lambda *args, **kwargs: {}
        with self.assertRaises(RuntimeError): bridge.resistance(bad, limits)

    def test_unfollowed_joint_aborts_before_another_goal(self):
        limits = {"maxFollowingErrorDegrees": 2}
        bridge.following_error({"joint.pos": 1}, {"joint.pos": 0}, limits)
        with self.assertRaises(RuntimeError): bridge.following_error({"joint.pos": 5}, {"joint.pos": 0}, limits)

    def test_upstream_typed_calibration_is_supported(self):
        Motor = make_dataclass("MotorCalibration", [("range_min", int), ("range_max", int)])
        result = bridge.bounded_action({"shoulder_pan.pos": 500}, {"shoulder_pan.pos": 0},
            {"shoulder_pan": Motor(1000, 3000)})
        self.assertLess(result["shoulder_pan.pos"], 90)

    def test_nested_upstream_configure_cannot_enable_before_seeding(self):
        events = []
        class Bus:
            is_connected = True
            motors = {"shoulder_pan": object()}
            def __init__(self):
                self.values = {"Torque_Enable": 0, "Torque_Limit": 1000}
            def read(self, key, name, normalize):
                return self.values[key]
            def write(self, key, name, value, normalize):
                self.values[key] = value
            def disconnect(self, disable_torque):
                self.is_connected = False
                events.append("disconnect")
            def disable_torque(self):
                self.values["Torque_Enable"] = 0
                events.append("disable")
            def enable_torque(self):
                self.values["Torque_Enable"] = 1
                events.append("enable")
            @contextlib.contextmanager
            def torque_disabled(self):
                self.disable_torque()
                try:
                    yield
                finally:
                    self.enable_torque()
            def sync_read(self, *args, **kwargs):
                return {"shoulder_pan": 30}
        class Follower:
            def __init__(self):
                self.bus = Bus()
                self.cameras = {}
                self.calibration = {"shoulder_pan": {"range_min": 100, "range_max": 3995}}
                self.is_calibrated = True
            def configure(self):
                with self.bus.torque_disabled():
                    events.append("configure")
                    self.bus.values["Torque_Limit"] = 500  # Configuration reset a runtime cap.
            def connect(self, calibrate=True):
                self.no_calibration = not calibrate
                self.configure()
            def get_observation(self):
                return {"shoulder_pan.pos": 10}
            def send_action(self, action):
                if "enable" in events:
                    raise RuntimeError("Torque enabled before the measured goal was seeded")
                if self.bus.values["Torque_Limit"] != 0:
                    raise RuntimeError("Position write auto-enables torque; output must be zero")
                self.bus.values["Torque_Enable"] = 1
                events.append("seed")
        module = types.ModuleType("lerobot.robots.so_follower.so_follower")
        module.SOFollower = Follower
        instances = []
        def run(module_name, run_name):
            robot = Follower()
            instances.append(robot)
            robot.connect()
        with patch.dict("sys.modules", {module.__name__: module}), patch.object(bridge, "validate_feetech_replies"), patch.object(bridge.runpy, "run_module", run), patch.object(bridge, "read_motor_limits", return_value={"maxLoadRaw": 100, "maxCurrentRaw": 100, "maxFollowingErrorDegrees": 2}):
            bridge.guarded_upstream("lerobot.scripts.lerobot_replay", [])
        self.assertTrue(instances[0].no_calibration)
        self.assertEqual(events, ["disable", "disable", "configure", "disable", "seed", "disable", "disable", "enable", "disconnect"])
        self.assertEqual(instances[0].bus.values["Torque_Limit"], 1000)

    def test_failed_zero_output_readback_never_enters_seed_body(self):
        class Bus:
            motors = {"joint": object()}
            def __init__(self):self.torque = 0;self.writes = []
            def disable_torque(self):self.torque = 0
            def read(self, key, name, normalize):return self.torque if key == "Torque_Enable" else 1000
            def write(self, key, name, value, normalize):self.writes.append((key,value))
        bus = Bus()
        with self.assertRaisesRegex(RuntimeError, "Zero drive output"):
            with bridge.zero_output_seed(bus):self.fail("Unverified output must never seed a goal")
        self.assertEqual(bus.torque, 0)
        self.assertTrue(all(key == "Torque_Limit" for key,value in bus.writes))

    def test_reply_length_guard_rejects_load_reply_misread_as_temperature(self):
        sdk = types.ModuleType("scservo_sdk")
        sdk.COMM_SUCCESS, sdk.COMM_RX_CORRUPT, sdk.INST_READ, sdk.INST_WRITE = 0,-7,2,3
        # Valid two-byte load reply can pass the upstream checksum/ID tests,
        # but cannot answer a one-byte temperature request.
        packets = [([255,255,1,4,0,57,4,189],0,0),
                   ([255,255,1,3,0,34,217],0,0),
                   ([255,255,1,2,0,252],0,0), (None,-6,0)]
        bus = types.SimpleNamespace(packet_handler=types.SimpleNamespace(txRxPacket=lambda *_:packets.pop(0)))
        mismatches = []
        with patch.dict("sys.modules", {"scservo_sdk":sdk}):
            bridge.validate_feetech_replies(bus,mismatches)
            with contextlib.redirect_stderr(__import__('io').StringIO()):
                self.assertEqual(bus.packet_handler.txRxPacket(None,[255,255,1,4,2,63,1,0])[1],-7)
            self.assertEqual(bus.packet_handler.txRxPacket(None,[255,255,1,4,2,63,1,0])[1],0)
            self.assertEqual(bus.packet_handler.txRxPacket(None,[255,255,1,4,3,40,0,0])[1],0)
            self.assertEqual(bus.packet_handler.txRxPacket(None,[255,255,1,4,2,63,1,0])[1],-6)
        self.assertEqual(len(mismatches),1)
        self.assertEqual(mismatches[0]["register"],63)

    def test_disconnect_releases_other_bus_after_one_fails(self):
        calls = []
        class Bus:
            is_connected = True
            def __init__(self, name, fail=False):
                self.name, self.fail = name, fail
            def disconnect(self, disable_torque):
                calls.append((self.name, disable_torque))
                if self.fail:
                    raise OSError("disconnected")
        robot = types.SimpleNamespace(bus1=Bus("left", True), bus2=Bus("right"), cameras={})
        with self.assertRaises(RuntimeError):
            bridge.disconnect(robot)
        self.assertEqual(calls, [("left", True), ("right", True)])

    def test_recorded_ranges_limit_policy_targets(self):
        values = {"shoulder_pan": {"range_min": 1000, "range_max": 3000}, "gripper": {"range_min": 100, "range_max": 3000}}
        result = bridge.bounded_action({"shoulder_pan.pos": 500, "gripper.pos": -20},
            {"shoulder_pan.pos": 0, "gripper.pos": 50}, values)
        self.assertLess(result["shoulder_pan.pos"], 90)
        self.assertGreater(result["gripper.pos"], 0)
        with self.assertRaises(ValueError):
            bridge.bounded_action({"shoulder_pan.pos": 0}, {"shoulder_pan.pos": 100}, values)
        with self.assertRaises(ValueError):
            bridge.bounded_action({"shoulder_pan.pos": math.nan}, {"shoulder_pan.pos": 0}, values)
        with self.assertRaises(ValueError):
            bridge.bounded_action({"unknown.pos": 0}, {"unknown.pos": 0}, values)

    def test_motor_overtemperature_stops_tool(self):
        class Bus:
            def sync_read(self, *args, **kwargs):
                return {"shoulder": 55}
        with self.assertRaises(RuntimeError):
            bridge.temperature(Bus())

    def test_linux_trigger_and_stick_ranges(self):
        xbox = bridge.Xbox.__new__(bridge.Xbox)
        xbox.axes = [(32768, 0, 65535, 0, 0, 0)] * 6
        self.assertEqual(xbox.get_axis(0), 0)
        xbox.axes[2] = (0, 0, 1023, 0, 0, 0)
        self.assertEqual(xbox.get_axis(2), -1)
        xbox.axes[2] = (1023, 0, 1023, 0, 0, 0)
        self.assertEqual(xbox.get_axis(2), 1)
        xbox.axes[4] = (-32768, -32768, 32767, 0, 0, 0)
        self.assertEqual(xbox.get_axis(4), -1)
        xbox.axes[4] = (1, 1, 1, 0, 0, 0)
        with self.assertRaises(ValueError):
            xbox.get_axis(4)

    def test_assets_cannot_escape_local_state(self):
        root = repo / ".build/evidence"
        root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root, prefix="robotics-paths-") as temp:
            state = Path(temp)
            (state / "policies/good").mkdir(parents=True)
            with patch.object(bridge, "STATE", state):
                self.assertEqual(bridge.local_path(str(state / "policies/good"), "policies"), state / "policies/good")
                with self.assertRaises(ValueError):
                    bridge.local_path("/etc", "policies")
                (state / "policies/escape").symlink_to("/etc", target_is_directory=True)
                with self.assertRaises(ValueError):
                    bridge.local_path(str(state / "policies/escape"), "policies")

    def test_mismatched_calibration_never_connects(self):
        root = repo / ".build/evidence"
        root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root, prefix="robotics-calibration-") as temp:
            state = Path(temp)
            (state / "calibration").mkdir()
            (state / "calibration/receipt.json").write_text(json.dumps({"robotId": "other", "leftPort": "left", "rightPort": "right"}))
            with patch.object(bridge, "STATE", state):
                with self.assertRaises(ValueError):
                    bridge.calibration({"robotId": "fixture", "leftPort": "left", "rightPort": "right"})

    def test_changed_devices_require_container_preparation(self):
        root = repo / ".build/evidence"
        root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root, prefix="robotics-mapping-") as temp:
            state = Path(temp)
            (state / ".build").mkdir()
            configured = {key: key for key in bridge.DEVICE_KEYS}
            (state / ".build/devices.json").write_text(json.dumps(configured))
            with patch.object(bridge, "STATE", state):
                bridge.prepared(configured)
                with self.assertRaises(ValueError):
                    bridge.prepared({**configured, "leftPort": "other-bus"})


if __name__ == "__main__":
    unittest.main()
