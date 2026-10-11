#!/usr/bin/env python3
"""Exercise the private phase adapter with fake buses, never real devices."""
import argparse
from dataclasses import dataclass, asdict
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types

ROOT = Path(__file__).resolve().parents[2]


@dataclass
class Calibration:
    id: int
    drive_mode: int = 0
    homing_offset: int = 0
    range_min: int = 1000
    range_max: int = 3000


class Bus:
    def __init__(self, names, scenario):
        self.motors = {name: types.SimpleNamespace(id=i + 1) for i, name in enumerate(names)}
        self.registers = {name: dict(Torque_Enable=0, Lock=0, Operating_Mode=1 if "wheel" in name else 0,
            Homing_Offset=0, Min_Position_Limit=1000, Max_Position_Limit=3000,
            Present_Position=2047, Present_Temperature=32) for name in names}
        self.initial = json.loads(json.dumps(self.registers))
        self.writes = []
        self.is_connected = False
        self.calibration = {}
        self.scenario = scenario
        self.sampling = False
        self.sample = 0

    def connect(self):
        self.is_connected = True

    def write(self, register, motor, value, *args, **kwargs):
        self.writes.append((register, motor, value))
        self.registers[motor][register] = value

    def read(self, register, motor, **kwargs):
        return self.registers[motor][register]

    def sync_read(self, register, motors=None, **kwargs):
        names = motors or list(self.motors)
        if register == "Present_Position" and self.sampling:
            self.sample += 1
            delta = min(300, self.sample * 60)
            if self.scenario == "narrow":
                delta = 4
            for name in names:
                self.registers[name][register] = 2047 + delta
        values = {name: self.read(register, name) for name in names}
        if self.scenario == "hot" and register == "Present_Temperature" and self.sampling:
            values[names[0]] = 45
        if self.scenario == "torque" and register == "Torque_Enable" and self.sampling:
            values[names[0]] = 1
        return values

    def disable_torque(self, motors=None):
        for name in motors or self.motors:
            self.write("Torque_Enable", name, 0)
            self.write("Lock", name, 0)

    def disconnect(self, disable_torque=True):
        if disable_torque:
            self.disable_torque()
        self.is_connected = False

    def read_calibration(self):
        return {name: Calibration(motor.id, homing_offset=self.read("Homing_Offset", name),
            range_min=self.read("Min_Position_Limit", name), range_max=self.read("Max_Position_Limit", name))
            for name, motor in self.motors.items()}

    def write_calibration(self, values, cache=True):
        for name, value in values.items():
            self.write("Homing_Offset", name, value.homing_offset)
            self.write("Min_Position_Limit", name, value.range_min)
            self.write("Max_Position_Limit", name, value.range_max)
        if cache:
            self.calibration = values

    def set_half_turn_homings(self, names):
        result = {}
        for name in names:
            self.write("Homing_Offset", name, 0)
            self.write("Min_Position_Limit", name, 0)
            self.write("Max_Position_Limit", name, 4095)
            result[name] = self.read("Present_Position", name) - 2047
            self.write("Homing_Offset", name, result[name])
        return result

    def record_ranges_of_motion(self, names, display_values=True):
        raise AssertionError("Phase adapter did not replace upstream terminal range input")


def fixture(state, scenario, upstream):
    module = types.ModuleType("bridge")
    module.STATE = state
    sys.modules["bridge"] = module
    joints = ("shoulder_pan", "shoulder_lift", "elbow_flex", "wrist_flex", "wrist_roll", "gripper")
    buses = []

    class Robot:
        def __init__(self, configuration):
            self.left_arm_motors = ["left_arm_" + j for j in joints]
            self.right_arm_motors = ["right_arm_" + j for j in joints]
            self.head_motors = ["head_motor_1", "head_motor_2"]
            self.base_motors = ["base_left_wheel", "base_back_wheel", "base_right_wheel"]
            self.bus1 = Bus(self.left_arm_motors + self.head_motors, scenario)
            self.bus2 = Bus(self.right_arm_motors + self.base_motors, scenario)
            self.calibration = {}
            self.calibration_fpath = state / "calibration/fixture.json"
            buses.extend((self.bus1, self.bus2))

        def _save_calibration(self):
            raise AssertionError("Upstream save bypassed review")

        def calibrate(self):
            # Same phase boundaries as the pinned upstream method. With
            # --upstream below, execute that installed method itself instead.
            for side, bus, names in (("left", self.bus1, self.left_arm_motors + self.head_motors),
                                     ("right", self.bus2, self.right_arm_motors)):
                bus.disable_torque()
                for name in names:
                    bus.write("Operating_Mode", name, 0)
                input("Move left arm and head motors to the middle" if side == "left" else "Move right arm motors to the middle")
                offsets = bus.set_half_turn_homings(names)
                bus.sampling = True
                minimum, maximum = bus.record_ranges_of_motion(names)
                bus.sampling = False
                values = {name: Calibration(bus.motors[name].id, homing_offset=offsets[name], range_min=minimum[name], range_max=maximum[name]) for name in names}
                if side == "right":
                    values.update({name: Calibration(bus.motors[name].id, range_min=0, range_max=4095) for name in self.base_motors})
                bus.write_calibration(values)
                self.calibration.update(values)
            self._save_calibration()

    if upstream:
        from lerobot_robot_xlerobot import XLerobot
        original = XLerobot.calibrate

        def calibrate_actual(self):
            # Mark sampling on entry to the adapter's replacement range method.
            for bus in (self.bus1, self.bus2):
                ranges = bus.record_ranges_of_motion

                def sample(names, display_values=True, _bus=bus, _ranges=ranges):
                    _bus.sampling = True
                    try:
                        return _ranges(names, display_values)
                    finally:
                        _bus.sampling = False

                bus.record_ranges_of_motion = sample
            original(self)

        Robot.calibrate = calibrate_actual
    package = types.ModuleType("lerobot_robot_xlerobot")
    package.XLerobot = Robot
    package.XLerobotConfig = lambda **kw: kw
    sys.modules["lerobot_robot_xlerobot"] = package
    spec = importlib.util.spec_from_file_location("xur_hand", ROOT / "tools/Xur.Robotics/hand_calibration.py")
    adapter = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(adapter)
    failure = None
    try:
        adapter.run(dict(robotId="fixture", motionEnabled=False, leftPort="left", rightPort="right"))
    except (Exception, KeyboardInterrupt) as error:
        failure = str(error)
    finally:
        (state / "bus-result.json").write_text(json.dumps(dict(failure=failure, buses=[dict(writes=bus.writes, initial=bus.initial, final=bus.registers) for bus in buses])))
    if failure:
        raise SystemExit(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture", type=Path)
    parser.add_argument("--scenario", default="success")
    parser.add_argument("--upstream", action="store_true")
    options = parser.parse_args()
    if options.fixture:
        fixture(options.fixture, options.scenario, options.upstream)
        return
    evidence = ROOT / ".build/evidence/hand-calibration-adapter"
    evidence.mkdir(parents=True, exist_ok=True)
    passed = []
    for scenario in ("success", "cancel", "hot", "torque", "narrow"):
        with tempfile.TemporaryDirectory(prefix=scenario + "-", dir=evidence) as temporary:
            state = Path(temporary)
            command = [sys.executable, __file__, "--fixture", str(state), "--scenario", scenario]
            if options.upstream:
                command.append("--upstream")
            process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            observed = []
            acknowledged = set()
            while line := process.stdout.readline():
                event = json.loads(line)
                observed.append(event["phase"])
                if scenario == "cancel" and event["phase"] == "left-range":
                    process.stdin.close()
                    break
                if scenario == "narrow" and event["phase"] == "left-range":
                    # Attempt to force a too-small range through the adapter.
                    process.stdin.write("left-range\n")
                    process.stdin.flush()
                    break
                if event["canAdvance"] and event["phase"] not in acknowledged:
                    process.stdin.write(event["phase"] + "\n")
                    process.stdin.flush()
                    acknowledged.add(event["phase"])
            process.wait(timeout=15)
            stderr = process.stderr.read()
            result = json.loads((state / "bus-result.json").read_text())
            assert process.returncode == (0 if scenario == "success" else 1), (scenario, stderr, result["failure"])
            writes = [write for bus in result["buses"] for write in bus["writes"]]
            assert all(register not in ("Goal_Position", "Goal_Velocity") and not (register == "Torque_Enable" and value) for register, _, value in writes)
            assert all(not ("wheel" in name and register not in ("Torque_Enable", "Lock")) for register, name, _ in writes), writes
            assert all(v["Torque_Enable"] == 0 for bus in result["buses"] for v in bus["final"].values())
            if scenario == "success":
                assert "review" in observed and "completed" in observed
                receipt = json.loads((state / "calibration/receipt.json").read_text())
                assert len(receipt["files"]) == 3 and receipt["method"] == "hand-guided"
            else:
                assert not (state / "calibration/receipt.json").exists()
                for bus in result["buses"]:
                    for name, value in bus["initial"].items():
                        assert all(bus["final"][name][key] == value[key] for key in ("Homing_Offset", "Min_Position_Limit", "Max_Position_Limit", "Operating_Mode")), (scenario, name)
            passed.append(scenario + ": phase boundary, torque off, stationary wheels, " + ("reviewed save" if scenario == "success" else "no approval and verified register restore"))
    result = dict(suite="HandCalibrationAdapter", upstream=options.upstream, passed=passed)
    (evidence / "result.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
