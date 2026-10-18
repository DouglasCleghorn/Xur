# Telemetry storage

Xur uses one local VictoriaMetrics single-node instance for time-series history.
GPU monitoring (15-second cadence), network monitoring (5-second cadence), and
model-benchmark GPU readings (2-second cadence) use the shared
`src/Xur.Domain/VictoriaMetrics.cs` codec. Collection continues without viewers.
CPU, memory and filesystem utilization in the system status are current snapshots,
not historical collectors. New histories must use this same storage boundary.

Configuration, trim/boot diagnostic events, benchmark settings, responses and
workload/process ownership snapshots remain documents. Robot recordings are
training datasets, not monitoring history. The robot motor diagnostics' five-minute
RAM window computes live temperature maxima/coverage; it is not retained or exposed
as a history, and remains independent of database availability.

Benchmarks also save a permanent, losslessly compressed result artifact at
`/var/lib/xur/benchmarks/<id>.json.gz`. The gzip payload is the versioned JSON
export (`schema`, `run`, `summary`), including settings, every captured GPU reading,
responses, context and errors. It is written once after sampling stops, including
cancelled/failed runs, with owner-only permissions, file fsync, atomic rename and
parent-directory fsync. Existing artifacts are never overwritten or automatically
expired. Saved-run reads and JSON exports prefer the artifact and work with VM
offline or after its retention expires; even an artifact restored without its
metadata index can be listed/read. Gzip uses .NET's `SmallestSize` compression and
requires no new runtime dependency. Export JSON still returns ordinary JSON.

The small `<id>.json` metadata/index remains for efficient listing and interrupted
run recovery. Numeric samples are stripped from that index; contextual snapshots
are checkpointed every 30 seconds and with responses/state changes. An abrupt
crash during an unfinished run can lose uncheckpointed context or unflushed VM
samples. Recovery marks it Interrupted, reconstructs available samples and saves
a permanent partial result once all indexed readings are available. Missing
readings are explicitly reported; recovery retries rather than freezing an
incomplete reconstruction into an immutable artifact. If writing a final artifact
fails, the full result stays in RAM with an error, and starting another benchmark
first retries saving it.

## Service and retention

The agent installs/repairs `xur-metrics.service` on installed-host startup, including
upgrades driven by older updaters. The managed unit includes the current bundle ID
so changing application bundles restarts storage against the bundled binary.
`PartOf=xur-agent.service` stops storage with the agent during application updates;
starting the agent starts storage again. VictoriaMetrics gets up to 120 seconds
for a clean shutdown. The live installer does not start it. A startup failure is
logged and monitoring reports history unavailability rather than taking management
offline. The service uses the current app bundle's `agent/victoriametrics/` binary.

The API listens only at `/run/xur-metrics/metrics.sock` inside a root-only directory.
There is no TCP listener, public query proxy or browser access to the storage API.
Data lives in `/var/lib/xur/metrics`. Local test clients can override the socket
with `XUR_METRICS_SOCKET`; this does not change the installed service listener.

Defaults are **12 months retention**, **30 minutes between guaranteed disk
flushes**, **64 precision bits**, a **128 MiB cache allowance** (not an RSS cap),
no rollup query cache, and no downsampling. Temporary snapshots expire after one
day so an interrupted migration cannot pin old parts indefinitely. The UI's existing 24-hour maximum
window is unchanged; older readings remain available in storage until retention
expires. Month-based retention expires partitions, so it is not an exact
365-day deletion deadline. Large buffers can also flush earlier under pressure.
These settings reduce frequent small writes, but background merges still write
data: this is not a promise of a particular SSD lifetime. A power loss, OOM or
SIGKILL can lose recent buffered samples. Clean shutdown persists them.

Normal collection pushes samples directly to VictoriaMetrics's RAM; it does not
force disk flushes or merges. Failed monitoring writes retry in a RAM-only queue
bounded to 32,768 readings per collector. Full queues drop oldest readings and
report the cumulative drop count. Recent monitoring samples also remain in the
existing bounded 24-hour RAM caches for live views during outages. Neither cache
is a second durable store. A failed benchmark write marks that sample explicitly
as unavailable in VM; its complete reading still goes into the permanent result
artifact when the run finishes.

## Metric schema

Metric names are `xur_<kind>_<field>`; labels are `kind`, `entity`, and `run`.
GPU entities use the PCI slot (matching the previous history identity); network
entities use interface names. A changed interface name or PCI slot creates a new
entity. The empty `run` identifies monitoring; an explicit benchmark uses its
run ID to isolate its short trace. Do not add timestamps, request IDs or process
IDs as labels to continuous monitoring. GPU performance state uses the bounded
`state` label on `xur_gpu_state`. Each known reading has a `present` sample, even
when all measurements are null. Missing fields remain null, never zero-filled
or interpolated; new fields/GPUs/interfaces need no historical rewrite.

Raw JSON-line import/export is only the transport: VictoriaMetrics owns its
compressed time-series files. Values are finite doubles at `precisionBits=64`.
Network byte counters are stored as high/low 32-bit halves, retaining integers
above 2^53 exactly. A submillisecond-tick field preserves original .NET timestamps;
the time-series identity is still entity plus millisecond (collectors sample much
more slowly). The 1 ms deduplication interval makes retries idempotent without
coarsening collector cadence. Queries reconstruct raw readings rather than using
PromQL range evaluation/interpolation. Each read is bounded to 64 MiB / 200,000 rows. Monitoring queries split current
entities into groups of eight so hosts with many interfaces stay within those
limits.

## Migration and recovery

The agent imports `gpu-history.json` once, verifies every retained GPU reading,
creates/deletes a VictoriaMetrics snapshot to force durable parts, then writes
`gpu-history.json.victoriametrics-imported`. It keeps the original JSON unchanged
and stops rewriting it. The control service similarly migrates all old benchmark
files in the background: it first archives their complete results (including
samples older than VM retention), then verifies the VM import and retains
`.json.legacy` originals before switching to metadata documents with
`telemetryVersion: 1`. If a previous metadata-only result lacks an archive,
recovery uses its `.json.legacy` original or reconstructs the trace from VM.
Failed imports remain retryable, even when an archive already exists;
imports only include timestamps within the new retention window and not in the
future. GPU history older than retention stays in the recovery original.

`/internal/force_flush` is used only to verify imports: it makes samples searchable
and **does not itself guarantee disk persistence**. The subsequent snapshot
checkpoint provides the durability barrier. Normal samples do not create snapshots.
If restoring the database independently of application state, also restore matching
migration markers/documents, or remove the GPU import marker and restore benchmark
`.json.legacy` files to `.json` to replay retained originals. Never delete original
recovery files automatically. Back up VictoriaMetrics with its snapshot/backup API;
finish snapshot-based backups within that one-day snapshot lifetime. Copying a
running data directory is not a consistent backup.

## Dependency and validation

`eng/toolchain-lock.json` pins the official VictoriaMetrics release archive and
matching source archive and Apache-2.0 license with checksums. `eng/prepare-metrics.py` prepares them under
ignored `.build/`; publication copies the runtime and notices into the agent's
application bundle, including vendored dependency notices. Installation and updates require no runtime download.

Focused checks (use the pinned SDK path from `eng/test-fast.sh`):

```sh
dotnet run --project tests/Xur.Unit.Tests -c Release -- --telemetry
python3 tests/Xur.Integration.Tests/victoriametrics.py
```

The integration test uses the pinned binary in an isolated ignored data directory,
checks actual import/export and restart persistence, and never touches the host's
installed telemetry service or database.
