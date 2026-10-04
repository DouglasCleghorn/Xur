# Update an existing installer USB

On Linux with .NET 10 and libarchive installed, run from this checkout:

```sh
./eng/update-usb.sh --check
./eng/update-usb.sh
```

The launcher uses sudo and runs the file-based C# app at
`eng/update-usb/app.cs`. It finds the removable FAT32 USB that already contains
Xur and exactly one `xur-diagnostics.yml` or `.yaml`. If several drives qualify,
select the partition explicitly with `--device /dev/sdX1`.
Command-line parsing and generated help use Microsoft's `System.CommandLine`,
matching Xur's CLI. Run `./eng/update-usb.sh --help` to list the options.

With .NET on your PATH, you can also run the app directly:

```sh
sudo dotnet run eng/update-usb/app.cs -- --check
sudo dotnet run eng/update-usb/app.cs
```

It selects the newest published GitHub release containing an x86_64 installer,
including Nightly releases, and skips newer releases containing only application
updates. The ISO streams from GitHub through libarchive directly into files on
the existing USB filesystem. No complete ISO is saved to disk or RAM, and the
drive is never formatted or repartitioned. Diagnostic and answer YAML files stay
byte-identical, including their existing keys; unrelated files and saved logs are
kept. Boot references are adjusted to the USB's existing volume label.

TeeForge 0.1.0 calculates SHA-256 while streaming. Downloads are checked against
GitHub's asset digest; releases with `installer.json` also require its signature
against this checkout's public key. Signed split media is streamed in part order
without assembling a local ISO. The app checks the full ISO digest and reads back
the copied files before reporting success, flushing only the USB filesystem.

The full download checksum is available only at the end of the stream. A failed
checksum, lost connection, unplugged drive or interrupted run can leave installer
files incomplete. Rerun the updater successfully before booting that USB. Its
configuration files are excluded from extraction and retained in memory during
the update. Keep the drive connected until the app reports it is unmounted.

`--check` checks the existing USB, current release and checksum metadata without
downloading the ISO or changing USB files. Device identity and configuration are
checked again immediately before writing. SSDs, whole disks, read-only media,
raw/DD ISO media and system disks are rejected.

The launcher keeps .NET build and NuGet caches in ignored `.build/usb-update/`
on disk. Small release descriptors and private mount points also go beneath
that ignored directory and are cleaned up afterward. ISO data uses small memory
buffers and writes only to the USB; there is no complete ISO file or staging
directory. Ubuntu needs `libarchive13` (or the distribution's equivalent
package); Fedora needs `libarchive`.
