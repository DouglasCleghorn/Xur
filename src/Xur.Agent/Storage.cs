using System.Text.Json;
using Xur.Domain;

namespace Xur.Agent;

public sealed class Storage
{
    public HashSet<string> Leases { get; } = [];
    volatile ScanResult scan = new("Starting", [], [], [], []);
    public ScanResult Scan => scan;
    public string? BootstrapToken { get; private set; }
    public AnswerConfiguration? Answer { get; private set; }
    public bool NetworkReady {get;private set;}=true;
    public void NetworkConfigured()=>NetworkReady=true;
    public bool CanPlan => NetworkReady && ( Scan.State == "NoAnswer" || Scan.State == "AnswerFound" && (BootstrapToken != null || Answer?.Network.Length>0));
    public static FileInfo[] AnswerFiles(string directory)=>new[]{"xur.yaml","xur.yml"}.Select(name=>new FileInfo(Path.Combine(directory,name))).Where(f=>f.Exists || f.LinkTarget!=null).ToArray();
    public static string? ReadBootstrapToken(string yaml)=>AnswerConfiguration.Parse(yaml).BootstrapToken;
    public void AnswerFailed(string message)
    {
        scan=scan with {State="Incomplete",Errors=[..scan.Errors,message]};
    }
    static string S(JsonElement x, string key) => x.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString().Trim() : "";
    static bool Volatile(JsonElement x) => System.Text.RegularExpressions.Regex.IsMatch(S(x,"path"),@"^/dev/(zram|ram)\d+$");
    public static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray()) foreach (var n in Flatten(child)) yield return n;
    }
    public static async Task<JsonElement[]> Nodes()
    {
        var result = await Processes.Run("lsblk", ["--json", "--bytes", "--paths", "--output",
            "NAME,PATH,TYPE,SIZE,MODEL,SERIAL,WWN,RO,FSTYPE,MOUNTPOINTS,UUID,PARTUUID,LABEL"]);
        if (result.ExitCode != 0) throw new IOException("Storage inventory failed");
        using var doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.GetProperty("blockdevices").EnumerateArray().Select(n => n.Clone()).ToArray();
    }
    public static bool IsBootMedia(JsonElement node, string commandLine)
    {
        // Preserve protection even when dracut has copied the live image to RAM
        // and unmounted the source. Match every parent of the identified source;
        // duplicated filesystem labels must never make an erase candidate.
        foreach (var argument in commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "inst.stage2=hd:";
            if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var source = argument[prefix.Length..];
            if (source.IndexOf(":/", StringComparison.Ordinal) is var separator && separator >= 0)
                source = source[..separator];
            source = System.Text.RegularExpressions.Regex.Replace(source, @"\\x([0-9a-fA-F]{2})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            var field = source.StartsWith("LABEL=", StringComparison.Ordinal) ? "label" :
                source.StartsWith("UUID=", StringComparison.Ordinal) ? "uuid" :
                source.StartsWith("PARTUUID=", StringComparison.Ordinal) ? "partuuid" : "path";
            var value = field == "path" ? source : source[(source.IndexOf('=') + 1)..];
            if (value.Length > 0 && Flatten(node).Any(n => S(n, field) == value)) return true;
        }
        return false;
    }
    static string[] Mounts(JsonElement node) => Flatten(node).SelectMany(n =>
        n.GetProperty("mountpoints").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
        .Select(v => v.GetString()!)).Distinct().Order().ToArray();
    static bool MountedReadOnly(string[] mountpoints)
    {
        var entries=File.ReadAllLines("/proc/self/mountinfo").Select(line=>line.Split(' ')).ToArray();
        static string Unescape(string path)=>System.Text.RegularExpressions.Regex.Replace(path,@"\\([0-7]{3})",m=>((char)Convert.ToInt32(m.Groups[1].Value,8)).ToString());
        return mountpoints.All(point=>entries.Any(fields=>fields.Length>6 && Unescape(fields[4])==point && fields[5].Split(',').Contains("ro")));
    }
    public async Task<Inventory> Observe()
    {
        var roots = await Nodes();
        var commandLine = await File.ReadAllTextAsync("/proc/cmdline");
        var disks = new List<Disk>();
        foreach (var node in roots.Where(n => S(n, "type") is "disk" or "rom"))
        {
            var path = S(node, "path"); var serial = S(node, "serial"); var wwn = S(node, "wwn");
            var blocked = new List<string>(); var mounts = Mounts(node);
            if(Volatile(node)) blocked.Add("Volatile RAM device; not persistent installation storage");
            var stable = Directory.Exists("/dev/disk/by-id") ? Directory.GetFiles("/dev/disk/by-id")
                .Where(p => !System.Text.RegularExpressions.Regex.IsMatch(p, @"-part\d+$"))
                .Order().FirstOrDefault(p => { try { return File.ResolveLinkTarget(p, true)?.FullName == path; } catch { return false; } }) ?? "" : "";
            if (S(node, "type") != "disk") blocked.Add("Optical/installer media; not an eraseable whole disk");
            if (mounts.Length != 0) blocked.Add("Mounted whole disk or child partition: boot/config/in-use media protected");
            if (IsBootMedia(node, commandLine)) blocked.Add("Installer boot source protected through whole-disk ancestry");
            if (Flatten(node).Any(n => Scan.Answers.Contains(S(n,"path")))) blocked.Add("Answer/configuration media protected through whole-disk ancestry");
            if (serial.Length == 0) blocked.Add("No stable serial");
            if (stable.Length == 0) blocked.Add("No resolvable /dev/disk/by-id identity");
            if (S(node, "ro") == "True" && !Leases.Contains(path)) blocked.Add("Device is read-only");
            long bytes = long.Parse(S(node, "size"));
            if (bytes < 64L * 1024 * 1024 * 1024) blocked.Add("At least 64 GiB required");
            var layout = Canonical.Hash(Flatten(node).Select(n => new { Path = S(n,"path"), Type = S(n,"type"),
                Bytes = S(n,"size"), Fs = S(n,"fstype"), Uuid = S(n,"uuid"), PartUuid = S(n,"partuuid") }).ToArray());
            disks.Add(new(path, stable, serial, wwn, S(node, "model"), bytes, layout, mounts, blocked.ToArray()));
        }
        var duplicates = disks.Where(d => d.Serial.Length != 0).GroupBy(d => d.Serial).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var observed = disks.Select(d => duplicates.Contains(d.Serial) ? d with { Blocked = [..d.Blocked, "Duplicate serial"] } : d).OrderBy(d => d.Path).ToArray();
        return new(Canonical.Hash(observed), observed);
    }
    public async Task DiscoverAnswers()
    {
        var answers = new List<string>(); var errors = new List<string>();
        var configurations = new List<AnswerConfiguration>();
        var readOnlyDevices = new List<string>(); var mounts = new List<ScanMount>();
        try
        {
            var nodes = (await Nodes()).Where(n => S(n,"type") is "disk" or "rom").Where(n=>!Volatile(n)).SelectMany(Flatten).DistinctBy(n => S(n,"path")).ToArray();
            // Set all eligible whole devices and their partitions read-only before any mount.
            foreach (var node in nodes)
            {
                var path = S(node,"path");
                if (!MountedReadOnly(Mounts(node)))
                { errors.Add($"{path}: mounted writable storage cannot be scanned safely"); continue; }
                var before = await Processes.Run("blockdev", ["--getro", path]);
                if (before.ExitCode != 0) { errors.Add($"{path}: cannot observe block read-only state"); continue; }
                if (before.Output.Trim() == "0")
                {
                    var locked = await Processes.Run("blockdev", ["--setro", path]);
                    if (locked.ExitCode != 0) { errors.Add($"{path}: cannot lock block device"); continue; }
                    Leases.Add(path);
                }
                var observed = await Processes.Run("blockdev", ["--getro", path]);
                if(observed.ExitCode == 0 && observed.Output.Trim() == "1") readOnlyDevices.Add(path);
                else errors.Add($"{path}: block read-only verification failed");
            }
            var index = 0;
            foreach (var node in nodes)
            {
                var fs = S(node,"fstype"); var path = S(node,"path");
                if (fs.Length == 0 || fs == "swap") continue; // Neither contains a mountable root directory.
                var readOnly = await Processes.Run("blockdev", ["--getro", path]);
                if (readOnly.ExitCode != 0 || readOnly.Output.Trim() != "1") { errors.Add($"{path}: not block read-only"); continue; }
                var options = fs switch {
                    "ext3" or "ext4" => "ro,noload,nosuid,nodev,noexec",
                    "xfs" => "ro,norecovery,nosuid,nodev,noexec",
                    "btrfs" => "ro,nologreplay,nosuid,nodev,noexec",
                    "ext2" or "vfat" or "exfat" or "ntfs" or "ntfs3" or "iso9660" or "udf" => "ro,nosuid,nodev,noexec",
                    _ => "" };
                if (options.Length == 0) { errors.Add($"{path}: unsupported or encrypted filesystem {fs}; discovery incomplete"); continue; }
                var mount = $"/run/xur/scan/{index++}"; Directory.CreateDirectory(mount);
                // A hybrid USB ISO can hold the parent block device open while exposing
                // separately scannable child filesystems. A read-only loop avoids the
                // kernel's exclusive filesystem claim on the original parent/children.
                var loopResult=await Processes.Run("losetup",["--find","--show","--read-only",path]);
                var loop=loopResult.Output.Trim();
                if(loopResult.ExitCode!=0 || !System.Text.RegularExpressions.Regex.IsMatch(loop,@"^/dev/loop\d+$"))
                { errors.Add($"{path}: cannot create read-only scan view"); continue; }
                var mounted=false;
                try
                {
                    var loopState=await Processes.Run("blockdev",["--getro",loop]);
                    if(loopState.ExitCode!=0 || loopState.Output.Trim()!="1")
                    { errors.Add($"{path}: scan view is not read-only"); continue; }
                    var result = await Processes.Run("mount", ["-t", fs == "ntfs" ? "ntfs3" : fs, "-o", options, loop, mount]);
                    if (result.ExitCode != 0) { var reason=Redaction.Logs(result.Output); errors.Add($"{path}: read-only mount failed: {reason[..Math.Min(reason.Length,512)]}"); continue; }
                    mounted=true;
                    var answerFiles=AnswerFiles(mount);
                    foreach(var file in answerFiles)
                    {
                        answers.Add(path);
                        if(file.LinkTarget!=null){errors.Add($"{path}: answer file must not be a symlink");continue;}
                        var kind=await Processes.Run("stat",["-c","%F","--",file.FullName]);
                        if(kind.ExitCode!=0 || kind.Output.Trim()!="regular file" || file.Length>32768)
                            errors.Add($"{path}: answer must be a regular file of at most 32 KiB");
                        else try{configurations.Add(AnswerConfiguration.Parse(await File.ReadAllTextAsync(file.FullName)));}
                        catch{errors.Add($"{path}: unsupported or invalid answer; installation locked");}
                    }
                    mounts.Add(new(path,fs,options,true,answerFiles.Any(f=>f.Exists && f.LinkTarget==null)));

                }
                finally
                {
                    if (mounted && (await Processes.Run("umount", [mount])).ExitCode != 0) errors.Add($"{path}: scanner unmount failed");
                    if ((await Processes.Run("losetup", ["--detach",loop])).ExitCode != 0) errors.Add($"{path}: scanner view cleanup failed");
                }
            }
        }
        catch { errors.Add("Storage discovery failed; installer remains locked"); }
        var completedScan = new ScanResult(answers.Count > 1 ? "Ambiguous" : errors.Count > 0 ? "Incomplete" : answers.Count == 1 ? "AnswerFound" : "NoAnswer",
            answers.ToArray(), errors.ToArray(), readOnlyDevices.Order().ToArray(), mounts.ToArray());
        Answer=completedScan.State=="AnswerFound" && configurations.Count==1 ? configurations[0] : null;
        BootstrapToken=Answer?.BootstrapToken;
        NetworkReady=Answer?.Network.Length is null or 0;
        scan=completedScan;
    }
    public async Task RestoreReadOnlyStates()
    {
        foreach (var path in Leases.Reverse())
            if ((await Processes.Run("blockdev", ["--setrw", path])).ExitCode != 0) throw new IOException("Cannot restore read-only lease");
        Leases.Clear();
    }
}
