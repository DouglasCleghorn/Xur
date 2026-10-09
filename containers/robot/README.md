# Robotics application container

Xur serves this ASP.NET Core minimal API app at `/robot` through its existing
authenticated web manager. The app is **Native AOT compiled**, with
reflection-based JSON disabled and source-generated JSON metadata. Its HTML,
CSS and JavaScript are local assets; rendering and operating the UI requires no
CDN or browser-library download.

The container owns the robot configuration, motor inventory, calibration checks,
controller sessions, demonstrations, training, reviewed skills, emotes, sorting
jobs and emergency-stop latch. Its .NET workflow invokes the pinned Python
LeRobot/XLeRobot adapter as a local subprocess. It does not call the host agent
to perform robot operations or launch another tools container.

The pages are:

- `/robot/`: head/hand camera snapshots, every motor's available registers,
  calibration assessment, controller start and recent operations.
- `/robot/setup`: device selection and automatic bus-role detection, effort
  limits, measured camera/tag metrology, session arming, demonstration recording,
  ACT training, skill evaluation and review, emotes and sorting tasks.
- `/robot/tags`: AprilTag corners over the exact final survey frame, duplicate-ID
  ambiguity, optional metric pose candidates from supplied measured settings,
  and copyable/downloadable JSON for all three frames from each camera.
- `/robot/controller`: the supported Xbox button map and session instructions.

All four pages include the software E-stop and reset controls. The two cameras
are captured separately; they are not synchronized stereo.

## Build and nightly publication

Build from the repository root:

```sh
docker build --file containers/robot/Containerfile --tag localhost/xur-robot:dev .
sudo python3 tests/Xur.Integration.Tests/robot-web.py --container localhost/xur-robot:dev --engine docker
```

Offline integration tests use disposable state and a fake upstream adapter,
without mapping robot hardware. The multistage image builds the .NET native
executable and installs the pinned LeRobot/XLeRobot, CPU ML, video and AprilTag
tools. The final image needs neither a .NET runtime/JIT nor an SDK. Python remains
an implementation dependency of the upstream robotics stack.

The restricted build context excludes credentials, evidence and generated output.
Microsoft images retain their explicit `mcr.microsoft.com` registry; Docker Hub
references use `mirror.gcr.io`, with no silent fallback. Upstream pins and notices
are retained with the tools, and Xur's MIT license and licensing guide ship in
the image.

`.github/workflows/robot.yml` follows Xur's channel convention: pushes to `main`
build and test a candidate, then the `nightly` environment publishes that exact
candidate to `ghcr.io/douglascleghorn/xur-robot:nightly` and `:sha-<commit>`.
Pull requests build/test without publishing. Manual runs publish only on `main`.
There is no stable image or release-branch publication. The package must be
public in GHCR for an unauthenticated appliance to pull it. Forks publish to their
owner's GHCR namespace.

## Appliance integration

The main Xur package is responsible for installing/loading/unloading the workload,
selecting its image, mounting its devices/state and proxying authenticated
requests. Robot settings and execution belong to this application.

Loading a Robotics workload starts `xur-robot-web` in an independent
`xur-robot-container` systemd service so agent upgrades preserve its lifetime.
Unloading first requests stop/disarm, verifies container exit, then removes it
and releases that service. A fresh container start pulls the selected GHCR
image; an already running healthy container keeps its image. Set
`XUR_ROBOT_IMAGE` in the agent environment to select a reviewed digest or prepared
`localhost/` image. Local images must already exist. Every selected image must
carry `io.xur.robot.runtime=container-v1`. The initial platform is `linux/amd64`.

Xur mounts its existing `/var/lib/xur/robotics` directory at `/state`, preserving
configuration, calibration, datasets, policies, private job evidence and the
E-stop latch across container updates. The workload mounts the robot device
inventory so setup can select stable serial/input/camera paths. Reconnect a
missing device and reload the Robotics profile if it is not present in the
container. Selecting a device does not enable its motors.

Camera setup accepts both `/dev/v4l/by-id/*-video-index0` and
`/dev/v4l/by-path/*-video-index0`. Inventory groups aliases by their resolved
capture node, preferring interface-specific by-path names and retaining each
distinct stream. It does not guess RGB/infrared or head/hand roles. Confirm the
selected image and decoded resolution; generic by-id aliases can collide on
multi-interface cameras. Changing either selected camera invalidates the old
calibration approval receipt while preserving range files and original recordings.
Measured intrinsics stay bound to their exact saved device path.

The container uses a private bridge network for outbound remote policy/backup
connections, with no published TCP port or host networking. It has no host agent
socket or container-engine socket and listens on its private application socket.
Its root filesystem is
read-only; state, temporary files and the application socket use dedicated
writable locations. Its access to serial, input and video devices is explicit;
it is not a privileged container. The Python adapter runs inside the same
container and cannot request arbitrary host commands.

Xur's `/robot` proxy keeps login, scoped API authorization, same-origin checks and
antiforgery validation. `/robot/csrf` supplies the logged-in browser's request
token. Session cookies, authorization and Tailscale identity headers are not
passed to the app. `/robot/api` exposes high-level tasks and skills, with strict
request schemas; clients cannot submit motor targets. Legacy `/robotics` redirects
to setup and `/api/robotics` relays to this same container for existing clients.

For a local native app build without a container:

```sh
dotnet publish containers/robot/Xur.Robot.csproj -c Release -r linux-x64 --artifacts-path .build/artifacts -o .build/robot/publish
python3 tests/Xur.Integration.Tests/robot-web.py --binary .build/robot/publish/Xur.Robot
```

Production listens at `XUR_ROBOT_SOCKET` (default
`/run/xur/robot-web/app.sock`). An explicit
`XUR_ROBOT_DEV_URL=http://127.0.0.1:8088` enables a development listener; keep this
manager-authentication bypass local to isolated tests. `XUR_ROBOT_STATE`,
`XUR_ROBOT_TOOLS` and `XUR_ROBOT_PYTHON` configure isolated runtime tests and
packaging paths. These environment variables are not browser-facing robot
settings.

## Current calibration and monitoring limits

**Auto calibrate currently runs a read-only automatic-calibration assessment.**
It checks motor inventories, stored ranges and repeated marker visibility, then
reports missing intrinsics, measured rigid marker transforms, joint references,
travel limits and a verified physical motor-power stop. Powered automatic
joint calibration is not implemented. The assessment does not create approval
receipts or treat existing EEPROM limits as verified mechanical limits. The
existing upstream hand-guided routine does not satisfy marker-only automatic
calibration. See the [design and acceptance gates](../../docs/architecture/robot-marker-calibration.md).

Camera/tag measurements are configured in the container app at `/robot/setup`,
with `GET/POST /robot/api/metrology`. No default intrinsics or tag dimensions are
invented. With measured device-specific values at the exact decoded resolution,
marker scans add the pinned AprilTag estimator's competing metric pose candidates,
positive-depth/rotation checks and raw-pixel reprojection error. Missing data
retains pixel-only scans; close planar fits remain explicitly ambiguous.
This never approves joints or enables motion. See the
[measurement format and proof scope](../../docs/architecture/robot-camera-metrology.md).

**Check installed tools** prepares device aliases for the selected hardware and
checks the container's installed upstream adapter. It does not download packages
or build another container. A selected controller is optional for camera checks.

**Start controller control** checks configuration, calibration and effort limits,
arms for 60 seconds and starts the upstream Xbox workflow. It rejects incomplete
setup and ends disarmed. Setup also supports an explicit short operator arming
session before demonstrations or skill evaluation. Wheels remain disabled.

Snapshots share the robotics ownership gate and are cached for five seconds
across viewers. They use read-only upstream motor access and disconnect without
changing torque. During calibration, controller use, recording or another
exclusive operation, the page retains the last snapshot with a paused/stale
timestamp. Continuous telemetry/camera streaming during motion remains future
work; the dashboard does not compete with the control loop for devices.

**E-STOP** cancels the active tool, requests upstream stop/disconnect cleanup and
persists a motion latch across application/container restarts. It acknowledges
the request before cleanup finishes and does not cut motor power. Keep a
physical motor-power disconnect available. **Reset E-stop** starts a fresh
feedback check of all seventeen motors; missing feedback, enabled torque,
movement or a status fault retains the latch. Reset leaves the robot disarmed
and never resumes an operation.

No trained picking policy ships in this image. Recording, CPU ACT training,
reviewed replay and high-level task execution provide the preparation workflow;
they still need physical acceptance on a calibrated robot. Training uses the
open-licensed LeRobot/ACT implementation without pretrained backbone weights.
Any future remote GPU policy configuration and rollout adapter belong to this
app, and must use open licenses for both implementation and model weights.


The requested Oculus Rift CV1/Touch teleoperation computer should reuse existing
upstream applications where compatible: first assess XLeVR and LeRobot's
[Isaac Teleop integration](https://huggingface.co/docs/lerobot/isaac_teleop).
The documented XLeVR browser flow targets Quest 3; neither that nor OpenXR support
alone verifies CV1 tracking on Windows/Linux. The current recorder uses Xbox
input. Reuse an existing VR application's native compositor and headset runtime
first. If a custom headset app is needed, Rust is an option for rendering, frame
timing and the native XR loop. Add a transport adapter only where an upstream
connection is missing. Robot settings, mapping and recording stay owned by the
ASP.NET Core app.
Original datasets stay under persistent `/state/datasets`. The app preserves
immutable snapshots and can automatically back them up to the separate xur-epyc
receiver, with manifests, checksums and visible status/errors on Setup. Configure
its HTTPS URL and dedicated token inside this app; see the
[receiver and recovery guide](../robot-backup/README.md). CV1 integration remains
unimplemented. Training and cleanup must preserve original recordings.
