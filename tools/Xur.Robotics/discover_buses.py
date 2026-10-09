"""Read-only upstream motor inventory, isolated from the motion adapter."""
import contextlib
from datetime import datetime, timezone
import json
import sys


def inventory(port, bus_factory=None):
    if bus_factory is None:
        from lerobot.motors.feetech import FeetechMotorsBus
        bus_factory = FeetechMotorsBus
    bus = bus_factory(port, {})
    try:
        bus.connect(handshake=False)
        # This sets the host UART rate, not a motor register.
        bus.set_baudrate(1_000_000)
        return bus.broadcast_ping(raise_on_error=True)
    finally:
        if bus.is_connected:
            # Default upstream disconnect writes torque registers. Discovery
            # must leave all motor state untouched, including on failure.
            bus.disconnect(disable_torque=False)


def health(port, models, bus_factory=None, motor_factory=None):
    """Sample effort and state through upstream reads; never configure torque."""
    if not models or any(model != 777 for model in models.values()):
        raise ValueError("Health capture requires the detected STS3215 inventory")
    if bus_factory is None:
        from lerobot.motors import Motor, MotorNormMode
        from lerobot.motors.feetech import FeetechMotorsBus
        bus_factory = FeetechMotorsBus
        motor_factory = lambda motor_id: Motor(motor_id, "sts3215", MotorNormMode.DEGREES)
    motors = {str(motor_id): motor_factory(motor_id) for motor_id in models}
    bus = bus_factory(port, motors)
    try:
        bus.connect(handshake=False)
        bus.set_baudrate(1_000_000)
        registers = {"positionRaw": "Present_Position", "loadRaw": "Present_Load",
                     "currentRaw": "Present_Current", "temperatureC": "Present_Temperature",
                     "voltageRaw": "Present_Voltage", "torqueEnabled": "Torque_Enable",
                     "moving": "Moving"}
        readings = [{"id": motor_id, "model": models[motor_id],
                     **{name: bus.read(register, str(motor_id), normalize=False)
                        for name, register in registers.items()}}
                    for motor_id in models]
        return {"observedAt": datetime.now(timezone.utc).isoformat(), "readings": readings}
    finally:
        if bus.is_connected:
            bus.disconnect(disable_torque=False)


if __name__ == "__main__":
    capture_health = sys.argv[1:] == ["--health", "/dev/candidate0", "/dev/candidate1"]
    ports = sys.argv[2:] if capture_health else sys.argv[1:]
    if ports != ["/dev/candidate0", "/dev/candidate1"]:
        raise ValueError("Use the two isolated discovery devices")
    with contextlib.redirect_stdout(sys.stderr):
        result = [inventory(port) for port in ports]
        if capture_health:
            result = [health(port, models) for port, models in zip(ports, result, strict=True)]
    json.dump(result, sys.stdout)
