# Robot dashboard container

Xur serves this ASP.NET Core minimal API app at `/robot`, through its existing
authenticated web manager. It is **Native AOT compiled**, with reflection-based
JSON disabled and source-generated JSON metadata. HTML, CSS and JavaScript are
local assets; no browser libraries or CDN access are required. The implementation
follows [ASP.NET Core's Native AOT support](https://learn.microsoft.com/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0).

The dashboard shows head and hand camera snapshots, all seventeen motor IDs and
readable state/configuration registers, session controls and the controller guide.
`/robot/tags` overlays AprilTag corners on the exact final frame of each survey,
shows duplicate-ID ambiguity, and provides copyable/downloadable JSON containing
all three frames from each camera. The two cameras are not synchronized stereo.

## Build and nightly publication

Build from the repository root:

```sh
docker build --file containers/robot/Containerfile --tag localhost/xur-robot:dev .
python3 tests/Xur.Integration.Tests/robot-web.py --container localhost/xur-robot:dev --engine docker
```

The multistage build uses the repository's .NET SDK version, clang and the Native
AOT compiler. The final image contains the native executable and runtime OS
dependencies, without the .NET runtime/JIT or SDK. The restricted build context
excludes credentials, evidence and generated output. Microsoft images retain their
explicit `mcr.microsoft.com` registry; Docker Hub references must use `mirror.gcr.io`.

`.github/workflows/robot.yml` follows Xur's channel convention: pushes to `main`
build and test a candidate, then the `nightly` environment publishes that exact
candidate to `ghcr.io/douglascleghorn/xur-robot:nightly` and `:sha-<commit>`.
Pull requests build/test without publishing. Manual runs publish only on `main`.
There is no stable image or release-branch publication. The package must be made
public in GHCR for an unauthenticated appliance to pull it, just like other public
workload images. Forks publish to their owner's GHCR namespace.

## Appliance integration

Loading a Robotics workload in a profile starts `xur-robot-web`. Unloading it first
stops/disarms robotics and releases its tools, then removes the web container.
The first load pulls the nightly image from GHCR if it is not cached. To select
a reviewed digest or a locally built image, set `XUR_ROBOT_IMAGE` in the agent's
environment. The initial supported image platform is `linux/amd64`.

The web container has no network, published port, device nodes or container-engine
socket. Its root filesystem is read-only. Xur mounts `/run/xur` read-only for the
private agent socket, with only `/run/xur/robot-web` writable for the app socket.
The trusted appliance component runs as root to access the root-only agent socket,
with all capabilities dropped and no privilege escalation. SELinux separation is
disabled for this container's private socket access; it is not given hardware.

Xur's `/robot` proxy keeps login, same-origin checks and antiforgery validation.
`/robot/csrf` supplies the logged-in browser's request token. Session cookies,
authorization and Tailscale identity headers are not passed to the container.
The app relays only an explicit set of high-level robotics endpoints to the agent;
it cannot relay arbitrary agent paths or accept motor targets.

For a local native build without a container:

```sh
dotnet publish containers/robot/Xur.Robot.csproj -c Release -r linux-x64 --artifacts-path .build/artifacts -o .build/robot/publish
python3 tests/Xur.Integration.Tests/robot-web.py --binary .build/robot/publish/Xur.Robot
```

Production listens at `XUR_ROBOT_SOCKET` (default `/run/xur/robot-web/app.sock`).
`XUR_AGENT_SOCKET` overrides the agent socket for isolated tests. An explicit
`XUR_ROBOT_DEV_URL=http://127.0.0.1:8088` enables a local development listener;
do not expose this bypass of the manager's authentication outside local testing.

## Current calibration and monitoring limits

**Auto calibrate currently runs the read-only automatic-calibration assessment.**
It checks motor inventories, stored ranges and repeated marker visibility, then
reports missing intrinsics, measured rigid marker transforms, joint references,
travel limits and a verified physical motor-power stop. The powered automatic
joint-calibration solver is not implemented. It never fabricates calibration
files/receipts or treats existing EEPROM limits as mechanically verified limits.

**Start controller control** atomically checks configuration/calibration/effort
limits, arms for 60 seconds and starts the upstream Xbox workflow. It rejects
unprepared hardware and ends disarmed. The guide documents the actual adapter
mapping, deadman button and stationary base.

Snapshots share the robotics ownership gate and are cached for five seconds
across viewers. They use upstream read-only motor access and disconnect without
changing torque. During calibration, controller use, recording or other exclusive
operations, the page explicitly retains the last snapshot with a paused/stale
timestamp. Continuous telemetry/camera streaming during motion is future work;
the dashboard does not compete with the control loop for serial or camera access.

This folder and workflow are source changes. Creating them does not push a branch,
publish an image or upgrade an installed manager.

Every robot page includes a sticky **E-STOP** button. It cancels the active tool,
requests the upstream stop/disconnect cleanup and persists a motion latch across
agent restarts. The button acknowledges the request before cleanup finishes; it
is a software stop and does not cut motor power. Keep a physical power disconnect
available. **Reset E-stop** starts a fresh feedback check of all seventeen motors;
missing feedback, enabled torque, movement or a status fault retains the latch.
Reset leaves the robot disarmed and never resumes the previous operation.
