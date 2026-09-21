# Files and time settings

These features now have separate maintained guides:

- [Files](files.md): storage and workstation tabs, navigation, sorting/filtering,
  streamed ZIP downloads, rename/move, deletion and folder-size caching.
- [Storage](storage.md): filesystem capacity, category accounting and SSD TRIM.
- [Timezone and NTP](timezone.md): automatic lookup, Cloudflare NTP and workload
  timezone behavior.
- [Graphics diagnostics](diagnostics.md): workstation device probes, NVIDIA
  mapping and collecting game logs without SSH.

Earlier preparation notes on this page described missing NVIDIA modeset nodes
and an unresolved white screen. Startup now initializes required NVIDIA nodes
before applying the selected GPU's device policy. The owner later confirmed
working game rendering, Moonlight input and sound on the RTX 3090. Fresh errors
still require current diagnostics; that confirmation is not a general hardware
or game compatibility guarantee.
