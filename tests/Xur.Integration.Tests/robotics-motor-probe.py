"""Verify bounded operator-attended identification before connecting hardware."""
import importlib.util
import contextlib
import io
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

repo = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("motor_probe", repo / "tools/Xur.Robotics/motor_probe.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class Bus:
    def __init__(self):
        self.values = {"Present_Position": 2000, "Present_Load": 0, "Present_Current": 0,
                       "Present_Temperature": 30, "Present_Voltage": 120, "Torque_Enable": 0,
                       "Status": 0, "Operating_Mode": 0, "Min_Position_Limit": 100,
                       "Max_Position_Limit": 3995, "Torque_Limit": 1000,
                       "Acceleration": 0, "Goal_Velocity": 0, "Goal_Position": 0}
        self.writes = []
        self.bad_cap = False
        self.bad_zero = False
        self.bad_current = False
        self.auto_enable = True
    def read(self, key, name, normalize=False):
        if key == "Torque_Limit" and ((self.bad_cap and self.values[key] == probe.OUTPUT_CAP) or (self.bad_zero and self.values[key] == 0)):
            return 1000
        if key == "Present_Current" and self.bad_current and self.values["Torque_Enable"]:
            return 31
        return self.values[key]
    def write(self, key, name, value, **kwargs):
        self.writes.append((key, value))
        self.values[key] = value
        if key == "Goal_Position" and self.auto_enable:
            self.values["Torque_Enable"] = 1


class Tests(unittest.TestCase):
    def test_console_keeps_adapter_lease_through_evidence_and_releases_owned_pid(self):
        with tempfile.TemporaryDirectory(dir=repo/".build/evidence") as folder:
            state = Path(folder)/"motor-identification"
            class Camera:
                observe = lambda *_:None
                def finish(self,directory):
                    self_test.assertTrue((state.parent/"adapter.json").exists())
                    with (state.parent/"adapter.lock").open("w") as contender:
                        with self_test.assertRaises(BlockingIOError):probe.fcntl.flock(contender,probe.fcntl.LOCK_EX|probe.fcntl.LOCK_NB)
                    return {}
            class ConnectedBus:
                def __init__(self,port,motors):self.motors=motors;self.is_connected=False
                def connect(self,**_):self.is_connected=True
                def set_baudrate(self,*_):pass
                def set_timeout(self,*_):pass
                def read(self,key,*_,**__):return {"Firmware_Major_Version":3,"Firmware_Minor_Version":10}.get(key,0)
                def disconnect(self,disable_torque):self_test.assertFalse(disable_torque);self.is_connected=False
            self_test = self
            modules = {
                "discover_buses":types.SimpleNamespace(inventory=lambda port:{i:777 for i in range(1,10 if 'right' in port else 9)}),
                "lerobot.motors":types.SimpleNamespace(Motor=lambda i,*_:types.SimpleNamespace(id=i),MotorNormMode=types.SimpleNamespace(DEGREES=0)),
                "lerobot.motors.feetech":types.SimpleNamespace(FeetechMotorsBus=ConnectedBus),
                "bridge":types.SimpleNamespace(validate_feetech_replies=lambda *_:None)}
            receipt = {"state":"completed","initial":{"position":2000},"samples":[],"torqueOffVerified":True}
            with patch.dict(sys.modules,modules),patch.object(sys,"argv",["motor_probe.py","right-shoulder-pan","--operator-present"]), \
                    patch.object(probe,"STATE",state),patch.object(probe,"CameraObserver",Camera),patch.object(probe,"preflight"),patch.object(probe,"probe",return_value=receipt),contextlib.redirect_stdout(io.StringIO()):
                probe.main()
            self.assertFalse((state.parent/"adapter.json").exists())
            with (state.parent/"adapter.lock").open("w") as contender:probe.fcntl.flock(contender,probe.fcntl.LOCK_EX|probe.fcntl.LOCK_NB)

    def run_probe(self, bus, vision=lambda phase: None):
        now = [0]
        def sleep(seconds): now[0] += seconds
        return probe.probe(bus, "shoulder_pan", vision, sleep=sleep, clock=lambda: now[0])
    def test_seed_and_cap_precede_torque_and_cleanup_restores_only_runtime(self):
        bus = Bus();result = self.run_probe(bus)
        self.assertEqual(result["state"], "completed")
        self.assertTrue(result["torqueOffVerified"])
        enable = bus.writes.index(("Torque_Enable", 1))
        seed = bus.writes.index(("Goal_Position", 2000))
        self.assertIn(("Torque_Limit", 0), bus.writes[:seed])
        self.assertIn(("Torque_Limit", probe.OUTPUT_CAP), bus.writes[:enable])
        self.assertIn(("Goal_Position", 2000), bus.writes[:enable])
        self.assertTrue(all(abs(v-2000) <= probe.DISPLACEMENT for k,v in bus.writes if k == "Goal_Position"))
        self.assertTrue(all(k in (*probe.SETTINGS, "Goal_Position", "Torque_Enable") for k,v in bus.writes))
        self.assertEqual(bus.values["Torque_Limit"], 1000)
        self.assertEqual(bus.values["Torque_Enable"], 0)
        self.assertEqual(bus.values["Goal_Position"], 2000)
        self.assertEqual(bus.writes[-1], ("Torque_Enable", 0))
        self.assertEqual(result["cleanupFeedback"]["torque"], 0)
        cap = 1000
        for key,value in bus.writes:
            if key == "Torque_Limit":cap = value
            if key == "Goal_Position" and value == 2000:self.assertLessEqual(cap, probe.OUTPUT_CAP)
    def test_cap_readback_failure_never_enables_torque(self):
        bus = Bus();bus.bad_cap = True;result = self.run_probe(bus)
        self.assertEqual(result["state"], "aborted")
        self.assertNotIn(("Torque_Enable", 1), bus.writes)
        self.assertTrue(result["torqueOffVerified"])
    def test_zero_output_readback_failure_never_sends_a_goal(self):
        bus = Bus();bus.bad_zero = True;result = self.run_probe(bus)
        self.assertEqual(result["state"], "power-cut-required")
        self.assertTrue(result["torqueOffVerified"])
        self.assertFalse(any(k == "Goal_Position" for k,v in bus.writes))
        self.assertNotIn(("Torque_Enable", 1), bus.writes)
    def test_narrow_range_and_already_enabled_joint_never_receive_writes(self):
        for changes in ({"Min_Position_Limit":1999,"Max_Position_Limit":2009},{"Torque_Enable":1},{"Operating_Mode":1}):
            bus = Bus();bus.values.update(changes)
            with self.assertRaises(RuntimeError): self.run_probe(bus)
            self.assertEqual(bus.writes, [])
    def test_effort_limit_stops_and_disables(self):
        bus = Bus();bus.bad_current = True;result = self.run_probe(bus)
        self.assertEqual(result["state"], "aborted")
        self.assertTrue(result["torqueOffVerified"])
        self.assertEqual(bus.values["Torque_Enable"], 0)
    def test_stale_vision_stops_and_disables(self):
        bus = Bus()
        def vision(phase):
            if phase is None: raise RuntimeError("Stale camera")
        result = self.run_probe(bus, vision)
        self.assertEqual(result["state"], "aborted")
        self.assertTrue(result["torqueOffVerified"])
    def test_operator_interrupt_stops_and_disables(self):
        bus = Bus()
        def interrupt(phase):
            if phase is None:raise KeyboardInterrupt("Operator stopped test")
        result = self.run_probe(bus,interrupt)
        self.assertEqual(result["state"],"aborted")
        self.assertTrue(result["torqueOffVerified"])
        self.assertEqual(bus.writes[-1],("Torque_Enable",0))
    def test_missing_feedback_stops_and_disables(self):
        bus = Bus();read = bus.read
        def missing(key, name, **kwargs):
            if key == "Present_Load" and bus.values["Torque_Enable"]: raise ConnectionError("No feedback")
            return read(key, name, **kwargs)
        bus.read = missing;result = self.run_probe(bus)
        self.assertEqual(result["state"], "aborted")
        self.assertTrue(result["torqueOffVerified"])
    def test_wheels_are_not_valid_selections(self):
        self.assertFalse(any("wheel" in name for name in probe.SELECTIONS))
        with self.assertRaises(ValueError): probe.probe(Bus(), "wheel_7", lambda _:None)
    def test_firmware_without_goal_auto_enable_also_cleans_up(self):
        bus = Bus();bus.auto_enable = False
        self.assertEqual(self.run_probe(bus)["state"], "completed")
        self.assertEqual(bus.values["Torque_Enable"], 0)
    def test_cleanup_setting_failure_still_finishes_with_torque_off(self):
        bus = Bus();write = bus.write
        def failing(key, name, value, **kwargs):
            if key == "Acceleration" and value == 0:raise ConnectionError("Restore failed")
            write(key,name,value,**kwargs)
        bus.write = failing;result = self.run_probe(bus)
        self.assertEqual(result["state"], "power-cut-required")
        self.assertTrue(result["torqueOffVerified"])
        self.assertEqual(bus.writes[-1], ("Torque_Enable", 0))
        self.assertEqual(bus.values["Torque_Limit"], 0)


if __name__ == "__main__": unittest.main()
