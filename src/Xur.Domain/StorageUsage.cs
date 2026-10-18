namespace Xur.Domain;
public record StorageFilesystem(string Source,string Type,string[] Mounts,long Bytes,long Used,long Available);
public record StorageEndurance(double? RemainingPercent=null,decimal? RemainingBytes=null,string? UnavailableReason=null);
public record StorageDiskUsage(string Path,string Model,long Bytes,string[] Filesystems,string[] Mounts,StorageEndurance? Endurance=null);
public record StorageUsageRow(StorageFilesystem? Filesystem,StorageDiskUsage[] Disks)
{
    // A shared filesystem appears once, with every observed backing disk. Mount
    // paths also match device-mapper aliases which differ between lsblk/findmnt.
    public static StorageUsageRow[] Build(StorageUsageSnapshot usage)
    {
        var rows=usage.Filesystems.Select(fs=>new StorageUsageRow(fs,usage.Disks.Where(d=>d.Filesystems.Contains(fs.Source)||d.Mounts.Intersect(fs.Mounts).Any()).ToArray())).ToList();
        var mounted=rows.SelectMany(r=>r.Disks).Select(d=>d.Path).ToHashSet();
        rows.AddRange(usage.Disks.Where(d=>!mounted.Contains(d.Path)).Select(d=>new StorageUsageRow(null,[d])));
        return rows.ToArray();
    }
}
public record StorageCategory(string Name,string[] Paths,long? Bytes,string? Error=null);
public record StorageUsageSnapshot(DateTimeOffset? CapturedAt,bool Scanning,StorageFilesystem[] Filesystems,StorageDiskUsage[] Disks,StorageCategory[] Categories,string? Error=null,ContainerVolume[]? Volumes=null);
