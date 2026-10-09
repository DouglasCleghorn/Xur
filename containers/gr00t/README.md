# GR00T N1.7 preparation for a remote Xur host

This is a preparation kit, not an enabled XLeRobot motion adapter. The container
uses NVIDIA's pinned GR00T implementation and its private policy protocol.
The [.NET console](../../tools/Xur.Robotics/gr00t/prepare.cs) creates a connection
plan, a private policy credential and an assessment of saved GPU inventory.
It issues no SSH, GPU, camera or motor commands.

On October 8, 2026, `xur-epyc.drum-goblin.ts.net` / `100.119.12.49` was present
in the tailnet but offline. Its GPU inventory, free resources, diagnostic access
and Hugging Face access have not been verified. Local synthetic preparation
checks do not establish that this image builds or runs on that host. No model
weights have been downloaded by the Xur preparation work.

## Reviewed versions and prerequisites

- Isaac-GR00T N1.7 source: `d2b7e75b937e3ec9aa5dbc798f08b89692c49734`.
- Base checkpoint: `nvidia/GR00T-N1.7-3B`, revision
  `2fc962b973bccdd5d8ce4f67cc63b264d6886495`.
- Gated backbone: `nvidia/Cosmos-Reason2-2B`, revision
  `9ce19a195e423419c349abfc86fd07178b230561`.
- Linux x86_64, Python 3.12 and CUDA 12.8, using upstream's frozen `uv.lock`.
- One available NVIDIA GPU with at least 16 GiB VRAM for the initial inference
  check. The preflight requires 16000 MiB usable total and currently free memory,
  allowing a small driver reservation below nominal 16 GiB capacity.
  Several smaller GPUs are not counted as one larger GPU.
- Upstream recommends 40 GB+ VRAM for fine-tuning. A passed inference assessment
  does not establish fine-tuning capacity. Its 40000 MiB candidate threshold
  likewise allows driver reservations on nominal 40 GiB devices.
- Podman plus NVIDIA Container Toolkit/CDI, with a selected GPU available to this
  job. Do not interrupt another Xur profile or allocate all GPUs by default.
- Authorized Hugging Face access to the Cosmos backbone and a read token in a
  private file. The tool does not accept model terms on the owner's behalf.
- Reserve adequate disk for the CUDA image, Python packages, model cache and
  datasets; inspect actual free space before building. The upstream stack is
  much larger than the robot dashboard image.

References: [NVIDIA installation](https://github.com/NVIDIA/Isaac-GR00T/blob/d2b7e75b937e3ec9aa5dbc798f08b89692c49734/README.md#installation),
[CUDA 12.8 driver table](https://docs.nvidia.com/cuda/archive/12.8.0/cuda-toolkit-release-notes/index.html),
[policy API](https://github.com/NVIDIA/Isaac-GR00T/blob/d2b7e75b937e3ec9aa5dbc798f08b89692c49734/getting_started/policy.md).
The inventory check uses the conservative Linux toolkit-driver baseline 570.26
and Ampere-or-newer compute capability for FlashAttention 2. Compatibility-mode
or different-architecture deployments need their own reviewed recipe.

## Tomorrow: inventory, build and cache

Run the console with the repository's .NET SDK. Generated files remain private
under `.build/`; re-running `plan` preserves the existing policy token:

```sh
dotnet run --file tools/Xur.Robotics/gr00t/prepare.cs \
  --artifacts-path .build/robotics/gr00t-cli -- \
  plan .build/robotics/gr00t-tomorrow
```

On xur-epyc, capture inventory without changing workloads:

```sh
mkdir -p .build/robotics/gr00t-tomorrow
nvidia-smi --query-gpu=uuid,name,memory.total,memory.free,driver_version,compute_cap \
  --format=csv,noheader,nounits > .build/robotics/gr00t-tomorrow/nvidia-smi.csv
nvidia-ctk cdi list
podman info

dotnet run --file tools/Xur.Robotics/gr00t/prepare.cs \
  --artifacts-path .build/robotics/gr00t-cli -- inventory \
  .build/robotics/gr00t-tomorrow/nvidia-smi.csv .build/robotics/gr00t-tomorrow
```

The assessment distinguishes a hardware candidate from verified GPU inference.
Do not upgrade host drivers or stop other profiles as part of this inventory.
Build from a source checkout on the server:

```sh
podman build --file containers/gr00t/Containerfile --tag localhost/xur-gr00t:n1.7 .
```

Docker Hub bases use `mirror.gcr.io`; a cache miss fails without a Hub fallback.
The restricted build context excludes runtime state, evidence and credentials.
The image retains upstream code, its lockfile and license notices; it never
updates its source or dependencies on startup. The initial recipe is x86_64.

Set these shell variables to reviewed local paths and the selected GPU UUID.
Create `HF_TOKEN_FILE` privately with mode 0600; never put its contents in shell
history, a Docker build argument, a profile environment variable or Git:

```sh
GR00T_GPU=GPU_REPLACE_WITH_SELECTED_UUID
GR00T_CACHE="$PWD/.build/robotics/gr00t-cache"
HF_TOKEN_FILE="$PWD/.build/robotics/gr00t-tomorrow/hf-token"
POLICY_TOKEN_FILE="$PWD/.build/robotics/gr00t-tomorrow/policy-token"
mkdir -p "$GR00T_CACHE"

podman run --rm --network=bridge --cap-drop=ALL \
  --security-opt=no-new-privileges \
  --volume "$GR00T_CACHE:/cache:rw,Z" \
  --volume "$HF_TOKEN_FILE:/run/secrets/hf-token:ro,Z" \
  localhost/xur-gr00t:n1.7 download
```

Cache preparation downloads both immutable revisions. It binds the backbone's
local offline `main` reference to the reviewed revision because the upstream
loader resolves the backbone by repository name. Failed/gated downloads remove
the preparation receipt. This step does not require a GPU, perform inference,
create a robot calibration receipt or certify XLeRobot compatibility.

## First GPU test, without the robot

Use the included DROID sample with its matching pretrained embodiment, not an
XLeRobot observation mislabeled as DROID. The command compares predictions with
saved demonstrations. It does not open serial devices or execute actions:

```sh
podman run --rm --network=none --cap-drop=ALL \
  --security-opt=no-new-privileges --security-opt=label=disable \
  --device "nvidia.com/gpu=$GR00T_GPU" --shm-size=2g \
  --volume "$GR00T_CACHE:/cache:ro" \
  --entrypoint python localhost/xur-gr00t:n1.7 \
  scripts/deployment/standalone_inference_script.py \
  --model-path /cache/models/n1.7 --dataset-path demo_data/droid_sample \
  --embodiment-tag OXE_DROID_RELATIVE_EEF_RELATIVE_JOINT \
  --traj-ids 1 2 --inference-mode pytorch --execution-horizon 8
```

If the upstream script needs an output path, mount an ignored evidence directory
and select that path. Keep runtime evidence and model weights out of Git.

## Private remote policy connection

After the GPU test passes, start the server with one selected GPU. Mount no robot
USB devices. Publish only on the host's loopback address:

```sh
podman run --name xur-gr00t --network=bridge --cap-drop=ALL \
  --security-opt=no-new-privileges --security-opt=label=disable \
  --device "nvidia.com/gpu=$GR00T_GPU" --shm-size=2g \
  --publish 127.0.0.1:5555:5555 \
  --volume "$GR00T_CACHE:/cache:ro" \
  --volume "$POLICY_TOKEN_FILE:/run/secrets/policy-token:ro" \
  localhost/xur-gr00t:n1.7 serve
```

The server uses upstream `PolicyServer` with a required token. Its remote kill
endpoint is removed. GR00T ZMQ is not an HTTP service and cannot be attached to
Xur's existing HTTP proxy as though it were one. Keep its unencrypted transport
inside loopback plus authenticated SSH; do not expose 5555 through Tailscale
Serve, a public listener, or an unauthenticated forwarding endpoint.

Xur's diagnostic SSH disables port forwarding and cannot provide this tunnel.
Provision a separate restricted forwarding account or reviewed authenticated
transport before connecting the robot container. Keep diagnostic SSH's existing
restrictions in place. On the client host, after that forwarding access is
established and its host key verified, establish a bounded tunnel with its
appropriate account and private key:

```sh
ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 \
  -o ServerAliveCountMax=3 -o StrictHostKeyChecking=yes \
  -L 127.0.0.1:15555:127.0.0.1:5555 gr00t-forward@xur-epyc.drum-goblin.ts.net
```

The generated `connection.json` describes this loopback endpoint and the separate
policy-token file. Copy that private token securely to the intended client;
never expose it in a URL. Test the service locally on the server with:

```sh
podman exec xur-gr00t python /opt/xur/server.py ping
```

This verifies a token-protected upstream ping, not prediction quality. The light
robot client, model latency/deadline handling and Xur task API rollout adapter
remain to be implemented and tested. Keep motion disabled in the meantime.

## XLeRobot adaptation work

N1.7's base checkpoint is not an XLeRobot picking policy. `NEW_EMBODIMENT` alone
does not make base weights compatible. Use calibrated single-arm demonstrations
first, preserving both head and hand videos, task text, joint/gripper ordering,
units and timing. Keep the head stationary and wheels excluded.

The existing Xur recorder writes LeRobot v3 data. The pinned GR00T workflow uses
LeRobot v2 plus `meta/modality.json`: use upstream's separately isolated
`scripts/lerobot_conversion` environment, then validate episode/frame alignment,
features and measured units. Do not invent end-effector poses from uncalibrated
encoders. The upstream SO100 example is a starting schema, not a certified
bimanual XLeRobot mapping or a pretrained SO101 checkpoint.

Follow [NVIDIA's custom-embodiment guide](https://github.com/NVIDIA/Isaac-GR00T/blob/d2b7e75b937e3ec9aa5dbc798f08b89692c49734/getting_started/finetune_new_embodiment.md)
with the selected modality config. Run open-loop evaluation of the fine-tuned
checkpoint, then supervised robot evaluation through XLeRobot/LeRobot tools.
Only that future local adapter may translate predictions into upstream robot
actions, with calibration/effort limits, stale-observation deadlines and E-stop
checks. The public assistant API should still request named tasks and skills.

`GR00T_MODE=custom` selects `NEW_EMBODIMENT` only with a different local fine-tuned
checkpoint under `/cache/models/`. That selection still grants no robot-motion
permission and supplies no missing physical calibration or skill review.
