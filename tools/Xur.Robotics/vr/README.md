# Rift CV1 / Touch preparation

Reviewed October 9, 2026. This is a reuse plan, not an installed VR client or a
motion endpoint. The headset computer/OS and its tracking runtime have not been
identified. Robot motion remains disabled until calibration and physical checks
pass. [reuse-plan.json](reuse-plan.json) records the reviewed source revisions,
known adaptation gaps and pending acceptance checks.

## Licensing requirement

Select application code under open-source licenses and model weights under
explicit open licenses, retaining their notices. Review weights separately from
the repository license; published source or downloadable weights alone do not
meet this requirement. This VR preparation selects no model or model weights.

CloudXR's SDK/runtime uses the
[NVIDIA CloudXR software agreement](https://developer.download.nvidia.com/cloudxr/EULA/NVIDIA_CloudXR_GA_License_without_Data_Collection_25Feb2025.pdf)
and is excluded from planned client dependencies. Reuse the openly licensed
LeRobot/Isaac clutch, kinematics and recording components with a compatible local
OpenXR reader; the default CloudXR example cannot be selected unchanged. No
CloudXR runtime has been installed by this preparation and no EULA has been
accepted.

Windows Meta/Oculus and SteamVR runtimes are proprietary, separate platform
compatibility routes. They are optional existing-runtime checks, not selected or
installed dependencies of this plan. Monado/OpenHMD provide open-source routes
to assess, but full CV1/Touch tracking remains unverified. The model/code license
requirement does not establish an open, working CV1 platform.

## Existing applications first

| Candidate | Reuse | CV1 acceptance status |
| --- | --- | --- |
| XLeVR | Existing A-Frame/WebXR renderer and controller reader, plus its transport format | Documented for Quest 3; test the CV1 through a desktop browser/runtime before selecting this path |
| LeRobot Isaac Teleop SO101 example | Openly licensed clutch, position-dominant IK, analog gripper mapping and dataset loop | Default CloudXR runtime is excluded; an open local reader/runtime integration still needs CV1 verification |
| Existing open-source VR app/compositor | Local headset rendering and camera panels if the browser path fails | Select after verifying the actual headset host/runtime and component licenses |

[XLeVR's pinned tutorial](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/docs/en/source/simulation/getting_started/vr_sim.md#L1-L3)
identifies Quest 3. Its
[monitor](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/XLeVR/README.md)
already separates controller acquisition from robot integration. Retain that
renderer/reader where compatible, rather than starting a new headset engine.

The reviewed [LeRobot example](https://github.com/huggingface/lerobot/blob/30da8e687a6dfc617fcd94afc367ac7071c376ce/examples/isaac_teleop_to_so101/README.md)
lives in its source tree, outside the pip package. It needs the kinematics extras
and optional Isaac dependencies, which are not installed in the current robotics
image. Its reviewed optional package reference is `isaacteleop~=1.3.131`; this is
not a selected installation because its default path requires CloudXR. The
current NVIDIA repository redirects to IsaacCapture. Keep the reviewed open
component APIs and source aligned rather than silently substituting the renamed
project's main branch or installing proprietary transitive dependencies.

## Runtime compatibility checks

If the headset host already uses Windows and a Meta/Oculus PC runtime, its CV1,
Touch pairing and Constellation sensor checks can establish an optional
compatibility route. Meta documents a
[PC OpenXR runtime](https://developers.meta.com/vr/documentation/native/pc/dg-openxr/),
and Valve provides a [Rift / SteamVR setup path](https://help.steampowered.com/en/faqs/view/17DA-EC4C-7D5B-8266).
Use the existing native runtime's own tracking/display test before testing
XLeVR in a desktop WebXR browser. Runtime support does not establish that a
particular browser, headset or controller binding works today.

For Linux, assess Monado's actual Rift build and optical tracking support.
The reviewed
[native Rift driver](https://gitlab.freedesktop.org/monado/monado/-/blob/ec188bb137b6af93120e015a8df100046a34d8f0/src/xrt/drivers/rift/rift_driver.c#L1281-1295)
initializes positional support as unavailable. This code evidence does not prove
that every alternative build lacks tracking, but prevents treating enumeration
as working 6DoF. [OpenHMD's current README](https://github.com/OpenHMD/OpenHMD/blob/85075b0c7e3c723ded2577edb79d00ee11aac339/README.md#L1-L10)
marks its main project unmaintained and points CV1 positional work to a fork.
Treat that route as experimental; verify both Touch translations, orientation,
button states and recovery from occlusion on the installed build. A display or
orientation-only headset test is insufficient for Cartesian robot control.

The [Isaac SO101 installation reference](https://github.com/huggingface/lerobot/blob/30da8e687a6dfc617fcd94afc367ac7071c376ce/examples/isaac_teleop_to_so101/README.md)
requires Linux and a supported CloudXR headset in its default workflow. That
workflow is excluded. Reusing its open clutch/IK/recording components needs a
separately verified local OpenXR input boundary. Opting out of its launcher only
avoids launch; it does not remove CloudXR dependencies or make a Windows Meta
runtime accessible from a Linux container.

## Container-owned mapping and recording

The intended path is:

```text
CV1 + Touch → local existing VR app/runtime → authenticated tracking input
                                                  ↓
                      robotics ASP.NET Core app: session, settings, freshness
                                                  ↓
                  upstream clutch → measured-pose rebase → SO101 IK → robot
                                                  ↓
                 existing LeRobot recorder: state, accepted action, cameras
                                                  ↓
                         originals → verified separate xur-epyc backup
```

This transport and VR source are not implemented yet. Reuse XLeVR's reader,
then adapt its input boundary inside the container. The main Xur package needs
only workload/device setup and authenticated proxy transport. Browser assets
must be restored locally through LibMan, with notices; XLeVR currently loads
A-Frame 1.7.1 from a CDN. Do not reuse its sample certificates/private keys or
expose a separate unauthenticated controller server.

Start with one selected arm. The
[upstream SO101 pipeline](https://github.com/huggingface/lerobot/blob/30da8e687a6dfc617fcd94afc367ac7071c376ce/examples/isaac_teleop_to_so101/common.py#L295-L408)
uses a clutch and IK for five arm degrees of freedom; orientation is a soft
constraint, while the gripper is a separate actuator. Reuse it with the verified
XLeRobot arm geometry and robot-specific workspace/effort limits. Confirm axis
and gripper conventions rather than assuming a six-axis industrial arm. Ignore
thumbsticks/base commands and headset-to-head motion; all three wheels stay off.

The app owns arming, the E-stop latch, input-source selection, transforms and
recording. A client sends tracking measurements and buttons, never motor IDs or
joint targets. Require finite poses, valid/tracked position and orientation,
ordered samples and a verified freshness deadline. Drop stale/duplicate frames
and release the clutch on tracking loss, disconnect or expired operator session;
reconnect never rearms. On grip engagement, rebase relative hand movement onto
measured arm pose. Verify an open-loop tracking display before connecting motors.

## Blockers before accepting any motor output

| Reviewed behavior | Required adaptation |
| --- | --- |
| XLeVR server sends absolute poses before its disabled squeeze block, [lines 284–355](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/XLeVR/xlevr/inputs/vr_ws_server.py#L284-L355) | Reuse the reader, bypass this motor-goal mapping, and enforce the container's held clutch and measured-pose rebase |
| XLeVR server inverts the trigger and closes on release, [lines 261–278](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/XLeVR/xlevr/inputs/vr_ws_server.py#L261-L278) and [474–494](https://github.com/Vector-Wangel/XLeRobot/blob/b017b5e6354bd9f61f4247a920c72622ca0aade0/XLeVR/xlevr/inputs/vr_ws_server.py#L474-L494), contrary to its UI | Normalize squeeze and trigger separately. Validate trigger polarity with no motors; trigger must close proportionally only during an authorized clutch session. Clutch release holds the gripper rather than closing it |
| Isaac recorder enables automatic reset by default, [lines 106–116](https://github.com/huggingface/lerobot/blob/30da8e687a6dfc617fcd94afc367ac7071c376ce/examples/isaac_teleop_to_so101/record.py#L106-L116) | Disable reset-to-origin and other automatic slews, seed from measured pose, and require explicit arming and grip engagement before movement |
| XLeVR includes simulation base controls; its client samples hidden controller objects | Disable all base output and reject untracked poses even if previous coordinates remain available |

The table records acceptance requirements, not fixes already applied upstream.
Do not run the unadapted robot-driving examples against xur-255.

## Rendering, video and dataset timing

Keep the existing compositor's headset frame loop independent of network input,
robot control and video decode. Measure the runtime's actual frame timing,
dropped frames and reprojection. Camera frames
can update more slowly without slowing head tracking. Start with separately
labelled head/hand video panels; these cameras are not a calibrated stereo pair.
The current five-second dashboard snapshots are inspection views, not suitable
VR teleoperation video. A low-latency, authenticated camera transport must be
added and measured before remote operation without direct robot sight.

Reuse existing native rendering/video facilities if WebXR does not meet the
measured frame budget. Rust is permitted if a custom headset application becomes
necessary for rendering, frame timing or the native XR loop. That decision waits
for an actual failure of the existing runtime/app path; this preparation creates
no custom renderer.

The current recorder saves state, accepted joint actions and both camera streams
at 10 Hz. A VR input source should feed that same recording boundary. Preserve
original recordings and add provenance for headset/runtime versions, mapping,
upstream revision, robot/camera identities and calibration hash. A supplementary
XR sidecar should retain sequence numbers, client sample times, server receipt
times, tracking flags and clutch events. Compare monotonic times only after
estimating clock offset and uncertainty; clocks on different computers are not
implicitly synchronized. Record camera capture times separately from receipt.

Include provenance and XR sidecars in the dataset checksum manifest and verified
remote backup. Conversion/fine-tuning may create derived datasets; they must not
rewrite/delete the originals. The robotics app implements verified remote
backup transport separately; see the
[receiver guide](../../../containers/robot-backup/README.md). VR timing sidecars
and headset input remain unimplemented.

## Next acceptance steps

1. Identify the headset host/OS/GPU, active runtime and CV1 sensors. Check the
   licenses of selected app/runtime components, then complete headset and both
   Touch tracking tests with robot motors off. Existing proprietary Windows
   runtimes remain optional compatibility checks, separate from app/model choices.
2. Probe desktop WebXR support from a secure page without starting motion:
   `await navigator.xr?.isSessionSupported('immersive-vr')`. A positive result is
   only a browser capability check; then test the existing renderer/input reader.
3. Confirm full controller translation, orientation, squeeze/trigger polarity,
   tracking-loss flags, input latency and headset rendering frame timing. Keep
   copies of diagnostics in private `.build/evidence/` storage.
4. Select the compatible reuse path. Add only missing authenticated transport,
   local assets and adapter integration inside the robotics app; test clutch,
   stale-frame rejection and stop behavior without motors first.
5. After calibration and verified backups are available, run a supervised,
   single-arm recording/evaluation with bounded upstream motion. No base motion.

Unknown headset host/OS, unverified CV1 tracking and absent container VR/video
transport currently prevent runtime acceptance. A source review does not remove
those blockers.
