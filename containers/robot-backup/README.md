# Verified recording backups

The robot app owns recording preservation, remote connection settings, retry
status and credentials. Xur's main package supplies the container and existing
`/robot` proxy; it does not manage backup settings.

After an upstream recording process releases its tools, the app copies all
available dataset files byte-for-byte into a local immutable snapshot. Original
files remain at `/state/datasets/<dataset>`. Snapshot files, manifests, status and
receiver credentials persist under `/state/backups`. Metadata and optional raw XR
sidecars are included; images/video are not reencoded. Manifests include the
recording outcome, calibration/camera provenance and available upstream versions.
The current recorder is Xbox; these backups do not imply VR recording is ready.

A successful session is labelled `completed`. An E-stop/tool failure also runs
the preservation hook and labels available samples `interrupted`; this does not
claim that a complete episode was saved. Missing or unstable files produce a
visible `snapshot-failed` result. The recorder independently fsyncs raw numeric
frames, timestamps, accepted actions, deadman flags and unmodified camera arrays
under `datasets/<dataset>/raw-recordings/<session>/` before handing frames to
LeRobot. This retains pending samples when upstream `save_episode()` is never
reached. Each session uses two append-only NumPy `.npy` frame streams, with
byte offset/length references in `frames.jsonl`; seek to an offset and use
`numpy.load(stream, allow_pickle=False)` to read one unchanged array. Files do not
multiply per frame. `observationReceivedAt` is the adapter's observation receipt
time, not a camera acquisition timestamp. This extra raw copy uses substantial
storage. Interrupted raw material is not a complete training
episode and is never automatically converted or resumed.

A durable history row is created before collection. On app restart, unfinished
collection is shown as `incomplete`; originals/raw journals remain for inspection.
A fully sealed local snapshot is checksum-checked and recovered after an
interrupted final rename/index update. Genuinely partial snapshot copies stay
visible as incomplete, with original and staging data retained for manual
inspection. Do not remove them while investigating. A power loss may leave the
last raw JSONL row truncated or a raw image without a referenced row; earlier
fsynced samples remain, and no complete episode is claimed.

Unconfigured remote backup is shown as `unconfigured`, never `verified`.
Configured uploads retry with persisted exponential backoff (10 seconds to an
hour) and resume already-uploaded content. Each attempt has a 30-minute deadline
covering body reads and uploads as well as connection setup; a stalled receiver
cannot hold upload ownership indefinitely. A manual retry is available on the
robot Setup page. File checksums are validated locally, during receiver upload,
and during atomic remote snapshot commit. Credentials are private and never
returned by the settings API. There are no delete, pruning or retention endpoints;
a failed or successful backup does not remove originals or earlier snapshots.

## Receiver on xur-epyc

The receiver is a separate Native AOT ASP.NET Core app. It needs persistent disk,
a dedicated random token, and an HTTPS endpoint. It needs no robot hardware,
GPU, container-engine socket, or host agent socket.

Generate a private token on the receiver machine; never place it in a command-line
argument or paste it into an issue/chat:

```sh
install -d -m 700 /var/lib/xur/robot-backups /var/lib/xur/robot-backup-secrets
umask 077
openssl rand -hex 32 > /var/lib/xur/robot-backup-secrets/token
```

Build this image from the repository root (publication is a separate authorized
release step):

```sh
podman build -f containers/robot-backup/Containerfile -t localhost/xur-robot-backup:prepared .
```

Run the receiver behind a loopback listener:

```sh
podman run --detach --name xur-robot-backup --pull=never --restart=no \
  --cap-drop=ALL --security-opt=no-new-privileges --read-only \
  --memory=512m --pids-limit=128 --tmpfs=/tmp:rw,nosuid,nodev,size=32m \
  --publish=127.0.0.1:7081:7081 \
  --volume=/var/lib/xur/robot-backups:/backup:rw \
  --volume=/var/lib/xur/robot-backup-secrets/token:/run/secrets/backup-token:ro \
  localhost/xur-robot-backup:prepared
```

On an SELinux host, use `:Z` for the dedicated backup directory and token file
mounts. Relabel only those paths. If backups use a separately mounted disk,
verify its mount and expected filesystem identity before each receiver start;
an absent disk must not redirect backups into the system disk.

Expose that dedicated listener through an approved HTTPS reverse proxy.
[Tailscale Serve documentation](https://tailscale.com/docs/reference/tailscale-cli/serve)
describes HTTPS ports and persistent background mode. On a
Tailscale host with HTTPS enabled, a separate Serve port can be used without
changing Xur's existing manager listener:

```sh
tailscale serve --bg --https=9443 http://127.0.0.1:7081
```

Verify the host's Serve configuration before adding this listener. Its ACL must
allow the robot to reach xur-epyc on TCP 9443. The client requires HTTPS and uses
normal certificate validation; it does not disable TLS checks or use diagnostic
SSH forwarding. If Serve is unavailable, configure a trusted TLS reverse proxy
for this receiver. The token authenticates only the receiver's snapshot API.

In the robot app, open `/robot/setup`, enter
`https://xur-epyc.drum-goblin.ts.net:9443/` and this dedicated receiver token under
**Recording backups**. These settings are saved only inside the robot container's
persistent state. No general automation API key is needed.

## Stored training data and recovery

The receiver stores content-addressed uploaded files under `/backup/blobs` and
only verified materialized snapshots under:

```text
/backup/snapshots/<manifest-sha256>/
  manifest.json
  receipt.json
  dataset/                 # Original relative filenames and unchanged bytes
```

An incomplete upload has no committed dataset. Resuming the same manifest
reuses validated blobs. Remote verification means that bytes match the snapshot;
it does not approve episode completeness, calibration, policy safety or labels.
For training, choose completed, reviewed snapshots; preserve interrupted material
for diagnosis rather than treating it as a successful demonstration.

The receiver refuses path traversal, symlinks in collected local data, conflicting
file paths, undeclared checksums, incorrect sizes and mismatched manifests.
Source originals and sealed snapshots are never cleaned up by this implementation.
Monitor disk capacity before lengthy recording sessions. Receiver-local staging
files from a power loss may require inspection; cleanup is deliberately manual.

To validate without hardware:

```sh
dotnet run --project tests/Xur.Robot.Backup.Tests -c Release
```

Production deployment and recording with real hardware must be verified
separately from these software tests.
