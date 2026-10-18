#!/usr/bin/env python3
"""Record gesture candidates in upstream XLeRobot MuJoCo, outside the robot app.

Uses the reviewed upstream model and its position actuators. Records simulated
feedback as well as commands; never opens a robot bus or creates calibration.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
import os
import subprocess
import zipfile
from pathlib import Path
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "tools/Xur.Robotics/upstream-lock.json"
NAMES = ("Rotation", "Pitch", "Elbow", "Wrist_Pitch", "Wrist_Roll", "Jaw")
EMOTES = ("wave", "welcome", "celebrate", "curious", "point")
HOME = (0, 1.1, 1.5, -.8, 0, .4)
FPS = 100
PREVIEW_FPS = 25


def digest(data):
    return hashlib.sha256(data).hexdigest()


def fetch_model(directory, pin):
    revision = pin["revision"]
    base = "https://api.github.com/repos/Vector-Wangel/XLeRobot"
    with urllib.request.urlopen(f"{base}/git/trees/{revision}?recursive=1", timeout=60) as response:
        tree = json.load(response)
    if tree.get("truncated"):
        raise RuntimeError("Upstream tree is incomplete; refusing an incomplete model.")
    files = [entry for entry in tree["tree"] if entry["type"] == "blob" and
             (entry["path"].startswith("simulation/mujoco/") or entry["path"] == "LICENSE")]
    if not any(entry["path"] == "LICENSE" for entry in files):
        raise RuntimeError("Upstream license is missing.")

    def download(entry):
        path = directory / entry["path"]
        blob = path.read_bytes() if path.exists() else b""
        git_hash = lambda data: hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        if git_hash(blob) != entry["sha"]:
            url = f"https://raw.githubusercontent.com/Vector-Wangel/XLeRobot/{revision}/{entry['path']}"
            with urllib.request.urlopen(url, timeout=60) as response:
                blob = response.read()
            if git_hash(blob) != entry["sha"]:
                raise RuntimeError("Upstream blob verification failed: " + entry["path"])
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(blob)
        return entry["path"], {"gitBlob": entry["sha"], "sha256": digest(blob)}

    with ThreadPoolExecutor(max_workers=6) as pool:
        receipt = dict(pool.map(download, files))
    (directory / "source.json").write_text(json.dumps({"repository": pin["url"], "revision": revision,
                                                     "files": receipt}, indent=2) + "\n")


def verify_model(directory, pin):
    receipt = json.loads((directory / "source.json").read_text())
    if receipt["revision"] != pin["revision"] or receipt["repository"] != pin["url"]:
        raise RuntimeError("Cached model is not the pinned XLeRobot revision. Run with --fetch.")
    for name, hashes in receipt["files"].items():
        path = (directory / name).resolve()
        if not path.is_relative_to(directory.resolve()) or digest(path.read_bytes()) != hashes["sha256"]:
            raise RuntimeError("Cached model changed: " + name)
    return receipt


def stationary_model(directory, output):
    """Freeze unused base/wheel/head joints while preserving upstream arm physics."""
    model = ET.parse(directory / "simulation/mujoco/xlerobot.xml").getroot()
    fixed = [joint.get("name") for joint in model.iter("joint")
             if joint.get("name") and not joint.get("name").endswith(("_L", "_R"))]
    equality = ET.SubElement(model, "equality")
    for name in fixed:
        ET.SubElement(equality, "joint", joint1=name, polycoef="0 0 0 0 0",
                      solref=".002 1", solimp=".999 .999 .0001")
    model.find("compiler").set("meshdir", str((directory / "simulation/mujoco/assets").resolve()))
    model_path = output / "stationary-xlerobot.xml"
    ET.ElementTree(model).write(model_path, encoding="unicode")
    scene = ET.parse(directory / "simulation/mujoco/scene.xml")
    scene.getroot().find("include").set("file", str(model_path))
    path = output / "scene.xml"
    scene.write(path, encoding="unicode")
    return path, fixed


def keyframes(emote, np, arm="right"):
    """Distinct, staged gestures in model radians, mirrored away from the body.

    Positive shoulder pitch raises the upstream hand. Each movement has enough
    time for quintic easing to stay below one degree per 10 ms recording frame.
    Expressive timing is simulated directly, with tracking and velocity guards.
    """
    rest = [0]*6
    if emote == "wave":
        # Raise above the elbow, then make two readable wrist/shoulder sweeps.
        raised = [-.25, .45, -.40, .40, 0, .08]
        outward = [-.33, .45, -.40, .40, -.14, .08]
        inward = [-.17, .45, -.40, .40, .14, .08]
        points = [(0, rest), (.72, raised), (1, outward), (1.36, inward),
                  (1.72, outward), (2.08, inward), (2.36, raised),
                  (3.12, rest), (3.40, rest)]
    elif emote == "welcome":
        # Open the empty gripper, extend it, then sweep outward to present space.
        offer = [-.10, .05, -.65, .40, .30, .20]
        present = [-.65, .05, -.65, .40, .60, .20]
        points = [(0, rest), (.84, offer), (1.56, present), (1.96, present),
                  (2.80, rest), (3.08, rest)]
    elif emote == "celebrate":
        # A high hand with two deliberate victory pumps; no gripper clapping.
        high = [-.28, .60, -.45, .35, 0, 0]
        low = [-.28, .38, -.30, .20, 0, 0]
        points = [(0, rest), (.72, high), (1, low), (1.28, high),
                  (1.56, low), (1.84, high), (2.04, high), (2.80, rest), (3.08, rest)]
    elif emote == "curious":
        # Bring the empty hand up, inspect both faces with a broad wrist turn.
        examine = [-.10, .35, -.20, .55, -.35, .12]
        turned = [-.10, .35, -.20, .55, .35, .12]
        points = [(0, rest), (.72, examine), (.88, examine), (1.72, turned),
                  (1.88, turned), (2.60, rest), (2.88, rest)]
    elif emote == "point":
        # A straightened arm holds a clear diagonal direction with quiet wrist.
        aim = [-.48, -.05, -.80, .35, 0, -.12]
        points = [(0, rest), (.96, aim), (1.44, aim), (2.40, rest), (2.68, rest)]
    else:
        raise ValueError("Unknown emote: " + emote)
    mirror = np.array([-1, 1, 1, 1, -1, 1]) if arm == "left" else np.ones(6)
    return [(t, np.array(HOME) + np.array(delta)*mirror) for t, delta in points]


def target_at(points, time):
    for (begin, a), (end, b) in zip(points, points[1:]):
        if time <= end:
            p = min(1, max(0, (time - begin) / (end - begin)))
            blend = p*p*p*(10 + p*(-15 + 6*p))
            return a + (b-a)*blend
    return points[-1][1]


def record(model, emote, arm, output, render, mujoco, np):
    data = mujoco.MjData(model)
    suffix = "L" if arm == "left" else "R"
    joints = [model.joint(name + "_" + suffix) for name in NAMES]
    addresses = np.array([joint.qposadr[0] for joint in joints])
    velocities = np.array([joint.dofadr[0] for joint in joints])
    controls = np.array([model.actuator(name + "_" + suffix).id for name in NAMES])
    other = np.array([model.joint(name + "_" + ("R" if suffix == "L" else "L")).qposadr[0] for name in NAMES])
    for side in ("L", "R"):
        for name, value in zip(NAMES, HOME):
            data.qpos[model.joint(name + "_" + side).qposadr[0]] = value
            data.ctrl[model.actuator(name + "_" + side).id] = value
    mujoco.mj_forward(model, data)
    for _ in range(round(3 / model.opt.timestep)):
        mujoco.mj_step(model, data)
    origin, other_origin = data.qpos[addresses].copy(), data.qpos[other].copy()
    initial_time = data.time
    points, frames, pictures = keyframes(emote, np, arm), [], []
    duration = points[-1][0]
    max_error = max_velocity = max_other_drift = max_step = 0.
    limits = np.array([joint.range for joint in joints])
    period = round(1 / FPS / model.opt.timestep)
    if abs(period*model.opt.timestep - 1/FPS) > 1e-10:
        raise RuntimeError("Model timestep cannot produce exact recording timestamps.")
    renderer = mujoco.Renderer(model, height=360, width=480) if render else None
    camera = mujoco.MjvCamera()
    camera.lookat[:] = [0, .19, 1.04]
    camera.distance, camera.azimuth, camera.elevation = 1.05, 65 if arm == "right" else 115, -15
    try:
        for step in range(round(duration / model.opt.timestep) + 1):
            time = step*model.opt.timestep
            command = target_at(points, time)
            if np.any(command <= limits[:, 0]+.03) or np.any(command >= limits[:, 1]-.03):
                raise RuntimeError("Gesture target approaches an upstream joint limit.")
            data.ctrl[controls] = command
            if not np.isfinite(data.qpos).all() or not np.isfinite(data.qvel).all():
                raise RuntimeError("Simulation became non-finite.")
            if data.ncon:
                contacts = [(model.body(model.geom_bodyid[c.geom1]).name,
                             model.body(model.geom_bodyid[c.geom2]).name, float(c.dist)) for c in data.contact]
                raise RuntimeError(f"Unexpected contact during {emote}/{arm} at {time:.3f}s: {contacts}")
            if np.any(data.qpos[addresses] < limits[:, 0]-.002) or np.any(data.qpos[addresses] > limits[:, 1]+.002):
                raise RuntimeError("Simulated feedback exceeds the model joint limits.")
            max_error = max(max_error, float(np.abs(data.qpos[addresses]-command).max()))
            max_velocity = max(max_velocity, float(np.abs(data.qvel[velocities]).max()))
            max_other_drift = max(max_other_drift, float(np.abs(data.qpos[other]-other_origin).max()))
            if max_error > .2 or max_velocity > 3 or max_other_drift > .002:
                raise RuntimeError("Simulated tracking, speed or inactive-arm check failed.")
            if step % period == 0:
                if frames:
                    max_step = max(max_step, float(np.abs(command-np.array(frames[-1]["action"])).max()))
                    if max_step > np.deg2rad(1):
                        raise RuntimeError(f"Recorded targets exceed a one-degree step at {FPS} Hz.")
                frames.append({"frame": len(frames), "timestamp": time,
                               "simulationTime": float(data.time-initial_time),
                               "action": command.tolist(), "observation.state": data.qpos[addresses].tolist(),
                               "observation.velocity": data.qvel[velocities].tolist(), "contacts": int(data.ncon)})
                if renderer and step % (period*(FPS//PREVIEW_FPS)) == 0:
                    from PIL import Image
                    renderer.update_scene(data, camera=camera)
                    pictures.append(Image.fromarray(renderer.render()).convert("P", palette=Image.Palette.ADAPTIVE))
            if step < round(duration / model.opt.timestep):
                mujoco.mj_step(model, data)
    finally:
        if renderer:
            renderer.close()
    return_error = float(np.abs(data.qpos[addresses]-origin).max())
    if return_error > .01:
        raise RuntimeError("Gesture did not settle back to its initial simulated pose.")
    directory = output / (emote + "-" + arm)
    directory.mkdir()
    (directory / "recording.jsonl").write_text("".join(json.dumps(frame, separators=(",", ":")) + "\n" for frame in frames))
    np.savez_compressed(directory / "trajectory.npz", timestamp=np.array([f["timestamp"] for f in frames]),
                        action=np.array([f["action"] for f in frames]),
                        state=np.array([f["observation.state"] for f in frames]),
                        velocity=np.array([f["observation.velocity"] for f in frames]))
    if pictures:
        pictures[0].save(directory / "preview.gif", save_all=True, append_images=pictures[1:], duration=1000//PREVIEW_FPS, loop=0)
    metadata = {"source": "XLeRobot MuJoCo simulation", "emote": emote, "arm": arm,
                "generatorSha256": digest(Path(__file__).read_bytes()),
                "jointNames": [name + "_" + suffix for name in NAMES], "positionUnits": "radians",
                "velocityUnits": "radians/second", "fps": FPS, "previewFps": PREVIEW_FPS if render else None, "durationSeconds": duration, "frames": len(frames),
                "maxCommandStepDegrees": float(np.rad2deg(max_step)), "maxFollowingErrorRadians": max_error, "maxVelocityRadiansPerSecond": max_velocity,
                "maxInactiveArmDriftRadians": max_other_drift, "returnErrorRadians": return_error,
                "contactCount": 0, "hardwareApproved": False, "physicalCalibrationHash": None,
                "transfer": "Candidate only. Model joint coordinates are not calibrated LeRobot motor degrees; measured mapping and supervised review are required.",
                "files": {p.name: digest(p.read_bytes()) for p in directory.iterdir()}}
    (directory / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
    return metadata


def export_bundle(directory, pin, simulation):
    """Package verified simulated data and a time-correct local MP4 preview.

    This export does not map model coordinates to motor coordinates. The app
    must import it as a preview-only candidate, never as a calibrated recording.
    """
    import imageio_ffmpeg
    from importlib.metadata import version
    expected = simulation["imageio-ffmpeg"]
    if version("imageio-ffmpeg") != expected:
        raise RuntimeError("Install the pinned imageio-ffmpeg export dependency.")
    metadata = json.loads((directory / "metadata.json").read_text())
    if metadata["hardwareApproved"] or metadata["physicalCalibrationHash"] is not None:
        raise RuntimeError("Simulation export cannot claim physical calibration or approval.")
    if metadata["positionUnits"] != "radians" or metadata["fps"] not in (10, 100):
        raise RuntimeError("Unexpected simulation recording units or cadence.")
    for name, checksum in metadata["files"].items():
        if Path(name).name != name or digest((directory / name).read_bytes()) != checksum:
            raise RuntimeError("Recording checksum failed: " + name)
    if "preview.gif" not in metadata["files"]:
        raise RuntimeError("A rendered preview is required; record with --render first.")
    # Preserve recorded timing. Legacy 5 Hz GIFs need 10 Hz encoding to
    # represent their half-frame endpoints; new 25 Hz renders encode directly.
    render_fps = metadata["previewFps"]
    if render_fps not in (5, 25):
        raise RuntimeError("Unexpected preview render cadence.")
    encoded_fps = 10 if render_fps == 5 else render_fps
    duration = metadata["durationSeconds"]
    video = directory / "preview.mp4"
    encoder = imageio_ffmpeg.get_ffmpeg_exe()
    subprocess.run([encoder, "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-i", str(directory / "preview.gif"), "-an", "-t", str(duration),
                    "-r", str(encoded_fps), "-c:v", "libx264", "-pix_fmt", "yuv420p",
                    "-crf", "20", "-movflags", "+faststart", str(video)], check=True)
    reader = imageio_ffmpeg.read_frames(str(video))
    video_info = next(reader)
    frame_count = sum(1 for _ in reader)
    if (frame_count != round(duration*encoded_fps) or abs(video_info["duration"]-duration) > .02
            or abs(video_info["fps"]-encoded_fps) > .001):
        raise RuntimeError("Encoded video timing does not match the simulated recording.")
    licenses = {"LICENSE": ROOT / "LICENSE", "licensing.md": ROOT / "docs/licensing.md",
                "XLeRobot-LICENSE": directory.parent / "XLeRobot-LICENSE"}
    for name, path in licenses.items():
        (directory / name).write_bytes(path.read_bytes())
    payloads = ("recording.jsonl", "trajectory.npz", "preview.mp4", "metadata.json", *licenses)
    fast = metadata["fps"] == 100
    identifier = metadata["emote"] + "-" + metadata["arm"] + ("-fast" if fast else "")
    manifest = {"schemaVersion": 1, "kind": "xur-emote-candidate", "id": identifier,
                "name": metadata["emote"].capitalize() + " · " + metadata["arm"].capitalize() + " arm" + (" (fast)" if fast else ""),
                "arm": metadata["arm"], "task": metadata["emote"],
                "coordinateSpace": "xlerobot-model-radians", "fps": metadata["fps"],
                "durationSeconds": duration, "jointNames": metadata["jointNames"],
                "source": {"repository": pin["url"], "revision": pin["revision"],
                           "simulator": "MuJoCo", "hardwareApproved": False},
                "hardwareApproved": False, "physicalCalibrationHash": None,
                "preview": {"file": "preview.mp4", "renderFps": render_fps,
                            "encodedFps": encoded_fps, "durationSeconds": duration,
                            "source": "Actual MuJoCo simulated joint feedback; stationary base and head."},
                "transfer": metadata["transfer"],
                "files": {name: digest((directory / name).read_bytes()) for name in payloads}}
    if "generatorSha256" in metadata:
        manifest["source"]["generatorSha256"] = metadata["generatorSha256"]
    manifest_bytes = (json.dumps(manifest, indent=2) + "\n").encode()
    bundle = directory.parent / (identifier + ".xuremot")
    temporary = bundle.with_suffix(".xuremot.tmp")
    with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", manifest_bytes)
        for name in payloads:
            archive.write(directory / name, arcname=name)
    with zipfile.ZipFile(temporary) as archive:
        if archive.testzip() is not None:
            raise RuntimeError("Exported archive failed its integrity check.")
        for name, checksum in manifest["files"].items():
            if digest(archive.read(name)) != checksum:
                raise RuntimeError("Exported payload checksum failed: " + name)
    temporary.replace(bundle)
    return {"file": str(bundle), "sha256": digest(bundle.read_bytes()),
            "durationSeconds": duration, "videoFrames": frame_count,
            "encoder": {"package": "imageio-ffmpeg", "version": expected,
                        "ffmpegVersion": imageio_ffmpeg.get_ffmpeg_version(),
                        "binarySha256": digest(Path(encoder).read_bytes())}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream", type=Path, default=ROOT / ".build/xlerobot-sim/upstream")
    parser.add_argument("--output", type=Path, default=ROOT / ".build/evidence/xlerobot-emotes")
    parser.add_argument("--fetch", action="store_true", help="Download and verify pinned upstream model/assets/license")
    parser.add_argument("--render", action="store_true", help="Also record MuJoCo preview GIFs (EGL by default)")
    parser.add_argument("--arm", choices=("left", "right", "both"), default="both")
    parser.add_argument("--emote", choices=(*EMOTES, "all"), default="all")
    parser.add_argument("--bundle", action="store_true", help="Export .xuremot files with local MP4 previews (requires --render)")
    parser.add_argument("--export-run", type=Path, help="Export bundles from an existing completed rendered run; no new simulation")
    options = parser.parse_args()
    for path in (options.upstream, options.output, *([options.export_run] if options.export_run else [])):
        if not any(path.resolve().is_relative_to(ROOT / folder) for folder in (".build", "dist")):
            parser.error("Simulation assets and recordings must remain under ignored .build/ or dist/ paths.")
    lock = json.loads(LOCK.read_text())
    if options.export_run:
        manifest = json.loads((options.export_run / "manifest.json").read_text())
        if manifest["revision"] != lock["xlerobot"]["revision"] or manifest["hardwareApproved"]:
            raise RuntimeError("Only pinned, unapproved simulation runs can be exported.")
        bundles = [export_bundle(options.export_run / (r["emote"] + "-" + r["arm"]),
                                 lock["xlerobot"], lock["simulation"]) for r in manifest["recordings"]]
        receipt = {"bundles": bundles, "hardwareApproved": False}
        (options.export_run / "exports.json").write_text(json.dumps(receipt, indent=2) + "\n")
        print(json.dumps(receipt, indent=2))
        return
    if options.bundle and not options.render:
        parser.error("--bundle requires --render so every candidate has an actual simulation preview.")
    if options.fetch:
        options.upstream.mkdir(parents=True, exist_ok=True)
        fetch_model(options.upstream, lock["xlerobot"])
    source = verify_model(options.upstream, lock["xlerobot"])
    if options.render:
        os.environ.setdefault("MUJOCO_GL", "egl")
    import mujoco
    import numpy as np
    if mujoco.__version__ != lock["simulation"]["mujoco"] or np.__version__ != lock["simulation"]["numpy"]:
        raise RuntimeError("Install the pinned simulation dependencies from upstream-lock.json.")
    options.output.mkdir(parents=True, exist_ok=True)
    run = options.output / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    run.mkdir()
    path, removed = stationary_model(options.upstream, run)
    model = mujoco.MjModel.from_xml_path(str(path))
    recordings = [record(model, emote, arm, run, options.render, mujoco, np)
                  for arm in (("left", "right") if options.arm == "both" else (options.arm,))
                  for emote in (EMOTES if options.emote == "all" else (options.emote,))]
    (run / "XLeRobot-LICENSE").write_bytes((options.upstream / "LICENSE").read_bytes())
    receipt = {"recordedAt": datetime.now(timezone.utc).isoformat(), "repository": source["repository"],
               "revision": source["revision"], "dependencies": lock["simulation"],
               "modelSha256": digest((run / "stationary-xlerobot.xml").read_bytes()),
               "sceneSha256": digest(path.read_bytes()), "upstreamFiles": source["files"],
               "adaptation": "Equality constraints hold base, wheel and head joints at zero; upstream geometry, inertias, actuators, limits and contact filters retained.",
               "fixedJoints": removed, "hardwareApproved": False, "recordings": recordings}
    (run / "manifest.json").write_text(json.dumps(receipt, indent=2) + "\n")
    if options.bundle:
        exports = [export_bundle(run / (r["emote"] + "-" + r["arm"]), lock["xlerobot"],
                                 lock["simulation"]) for r in recordings]
        (run / "exports.json").write_text(json.dumps({"bundles": exports, "hardwareApproved": False}, indent=2) + "\n")
    print(json.dumps({"output": str(run), "recordings": len(recordings), "framesByRecording": {r["emote"] + "-" + r["arm"]: r["frames"] for r in recordings},
                      "upstreamRevision": source["revision"], "hardwareApproved": False}, indent=2))


if __name__ == "__main__":
    main()
