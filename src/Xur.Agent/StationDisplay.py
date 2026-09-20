"""Resize only Xur's virtual monitor, as the workstation user. No root access."""
import json
import os
import subprocess
import sys

OUTPUT = "Virtual-Xur-Stream"

def run(*args):
    # Sunshine deliberately uses Qt offscreen. KScreen needs the Wayland QPA
    # integration to enumerate outputs; inheriting offscreen waits indefinitely.
    environment = dict(os.environ, QT_QPA_PLATFORM="wayland")
    result = subprocess.run(["/usr/bin/kscreen-doctor", *args], env=environment, capture_output=True, text=True, timeout=12)
    if result.returncode:
        raise RuntimeError("KDE could not apply the virtual display mode: " + (result.stderr + result.stdout)[-1500:])
    return result.stdout

def observe():
    outputs = json.loads(run("--json"))["outputs"]
    matches = [o for o in outputs if o.get("name") == OUTPUT and o.get("connected") and o.get("enabled")]
    if len(matches) != 1:
        raise RuntimeError("Xur's virtual monitor is unavailable. Load a headless workstation first.")
    return matches[0]

def validate(width, height, fps):
    width, height, fps = int(width), int(height), int(fps)
    if not (320 <= width <= 7680 and 240 <= height <= 4320 and width % 2 == height % 2 == 0 and 10 <= fps <= 240):
        raise ValueError("Use even dimensions from 320×240 to 7680×4320 and 10–240 Hz.")
    return width, height, fps

def find_mode(output, width, height, fps):
    # KDE derives CVT timings: a requested 30 Hz mode can report 29.644 Hz.
    # Match the nearest nominal rate without adding the same mode repeatedly.
    modes = [m for m in output["modes"] if m["size"] == {"width": width, "height": height} and abs(m["refreshRate"]-fps) <= max(.5, fps*.01)]
    return min(modes, key=lambda m: abs(m["refreshRate"]-fps), default=None)

def resize(width, height, fps):
    width, height, fps = validate(width, height, fps)
    output = observe()
    mode = find_mode(output, width, height, fps)
    if mode is None:
        run(f"output.{OUTPUT}.addCustomMode.{width}.{height}.{fps*1000}.full")
        mode = find_mode(observe(), width, height, fps)
    if mode is None:
        raise RuntimeError("KDE did not accept this custom mode. The current display was retained.")
    # Use only the numeric mode ID supplied by KScreen, never caller-provided commands.
    mode_id = str(mode["id"])
    if not mode_id.isdecimal():
        raise RuntimeError("KDE returned an invalid mode identifier.")
    run(f"output.{OUTPUT}.mode.{mode_id}")
    actual = observe()
    selected = find_mode(actual, width, height, fps)
    if selected is None or str(actual["currentModeId"]) != str(selected["id"]):
        raise RuntimeError("KDE did not switch to the requested virtual display mode.")
    return actual

def main():
    if sys.argv[1:] == ["--moonlight"]:
        result = resize(*(os.environ["SUNSHINE_CLIENT_"+key] for key in ("WIDTH", "HEIGHT", "FPS")))
    elif sys.argv[1:] == ["--status"]:
        result = observe()
    elif len(sys.argv) == 4:
        result = resize(*sys.argv[1:])
    else:
        raise ValueError("Expected --status, --moonlight, or width height fps.")
    print(json.dumps(result))

if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, RuntimeError, subprocess.SubprocessError, OSError) as error:
        print(str(error), file=sys.stderr)
        # A client with an unsupported mode must still be able to reach its desktop.
        sys.exit(0 if sys.argv[1:] == ["--moonlight"] else 1)
