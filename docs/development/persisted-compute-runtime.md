# Persisted Compute runtime

The initial implementation provides shared, durable container run intent and
uses T3 Code as its first application. It reuses `WorkloadRuntime` and its host
allocation locks, container identity checks, health checks and volume handling.
It does not add a competing container launcher.

`/var/lib/xur/persisted-compute.json` records each service's workload, enabled
state, revision, failure count, next retry and last error. Writes flush a private
temporary file before atomic replacement. Stopping a service removes its runtime
receipt/container, retaining the service definition and named volumes. Original
T3 runtime IDs are adopted when present; otherwise a prepared T3 recipe supplies
the image and existing volume. Several unrelated volumes require explicit
selection through the agent's `/persisted-compute/t3-code` endpoint with a
`recipeId`; discovery never guesses between different environments.

The agent reconciles enabled services after startup, installation and crashes.
Failed starts back off for 5, 10, 20 and 40 seconds; the fifth failure requires
Retry. The failure count survives agent restarts. Disabled intent cancels an
in-flight startup or pending retry. Native container restart policies are set to
`no` so they cannot bypass this intent or the retry limit. Application update
maintenance suspends reconciliation; healthy containers remain in their existing
systemd units through agent restarts. No recurring schedule is created.

T3 installation defaults to enabled. The authenticated T3 page changes this
setting through `POST /api/t3-code/enabled`. The runtime reports actual state and
errors separately from intent. The page has no profile lifecycle controls or
link to a separate workload manager. Direct T3 connections still use port 3773.

Persisted runtime receipts share the host runtime's allocation authority but are
excluded from observations used for profile planning. Old profile stop commands
cannot stop a persisted receipt. T3 is removed from profile selection and from
legacy profile plans; repaired profile revisions retain their original history.
A model chosen for T3 still follows its own inference profile. This does not
make the T3 service itself depend on that profile.

The updater's `persisted-compute-v1` compatibility guard prevents activation of
older releases once service intent exists. Existing container and T3 recipe
compatibility guards remain in place.

Validation covers default startup, off/on persistence, simulated reboot,
interrupted startup, bounded retry and retry cancellation, identity and volume
adoption, old profile stop isolation, preserved revision history and rollback
compatibility. Real host reboot and container execution require target-host
validation; fixture tests do not claim that coverage.

This is the lifecycle foundation, not completion of every proposal in the
[design](persisted-compute.md). The general service-management page, GPU/resource
overrides, usage attribution, Tailscale service origins and operation-scoped
Robotics peripheral ownership remain future work. Robotics is not migrated by
this change. The shared lifecycle currently accepts prepared containers without
exclusive GPU allocations; T3's existing runtime supplies its shared GPU access.
