using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Xur.Robot.Backups;

public record BackupFile(string Path,long Size,string Sha256);
public record BackupManifest(int Version,string RecordingId,string Dataset,string RobotId,string Arm,string Task,DateTimeOffset CompletedAt,BackupFile[] Files,BackupProvenance? Provenance=null,string Outcome="completed");
public record BackupProvenance(string InputSource,string? LeRobotVersion=null,string? XLeRobotRevision=null,string? CalibrationHash=null,string? HeadCamera=null,string? HandCamera=null);
public record BackupRemoteStatus(string SnapshotId,string State,string[] MissingBlobs,DateTimeOffset? VerifiedAt=null);
public record BackupError(string Error);
public record BackupEmptyRequest();
public record BackupSettingsRequest(string Url,string Token="");
public record BackupSettings(string Url,bool TokenStored);
public record BackupPrivateSettings(string Url,string Token);
public record BackupRetryRequest(string RecordingId);
public record RecordingBackupStatus(string RecordingId,string Dataset,string SnapshotId,string State,DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,int Attempts=0,string? Error=null,DateTimeOffset? NextAttemptAt=null,string? Destination=null,DateTimeOffset? VerifiedAt=null,string Outcome="completed");
public record BackupReceipt(string SnapshotId,string Destination,DateTimeOffset VerifiedAt);

[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(BackupRemoteStatus))]
[JsonSerializable(typeof(BackupError))]
[JsonSerializable(typeof(BackupEmptyRequest))]
[JsonSerializable(typeof(BackupSettingsRequest))]
[JsonSerializable(typeof(BackupSettings))]
[JsonSerializable(typeof(BackupPrivateSettings))]
[JsonSerializable(typeof(BackupRetryRequest))]
[JsonSerializable(typeof(RecordingBackupStatus))]
[JsonSerializable(typeof(RecordingBackupStatus[]))]
[JsonSerializable(typeof(BackupReceipt))]
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow)]
public partial class BackupJson:JsonSerializerContext
{
    public static JsonTypeInfo<T> Type<T>()=>(JsonTypeInfo<T>)Default.GetTypeInfo(typeof(T))!;
    public static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,Type<T>());
    public static T Read<T>(byte[] value)=>JsonSerializer.Deserialize(value,Type<T>())??throw new InvalidOperationException("Empty backup document.");
}

public static class BackupProtocol
{
    public const long MaxFileBytes=64L*1024*1024*1024;
    public const int MaxManifestBytes=2*1024*1024;
    public static bool Hash(string? value)=>value!=null&&Regex.IsMatch(value,@"\A[a-f0-9]{64}\z");
    public static bool RecordingId(string? value)=>value!=null&&Regex.IsMatch(value,@"\A[a-f0-9]{32}\z");
    public static bool Name(string? value)=>value!=null&&Regex.IsMatch(value,@"\A[a-z][a-z0-9-]{0,47}\z");
    public static string Digest(ReadOnlySpan<byte> value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    public static string SnapshotId(BackupManifest manifest)=>Digest(BackupJson.Bytes(manifest));
    public static void Validate(BackupManifest value)
    {
        if(value.Version!=1||!RecordingId(value.RecordingId)||!Name(value.Dataset)||!Name(value.RobotId)
            ||value.Arm is not ("left" or "right")||string.IsNullOrWhiteSpace(value.Task)||value.Task.Length>500
            ||value.Outcome is not ("completed" or "interrupted")||value.CompletedAt==default||value.Files is not {Length:>0 and <=10000})
            throw new InvalidOperationException("Invalid recording snapshot manifest.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var sizes=new Dictionary<string,long>();long total=0;
        foreach(var file in value.Files)
        {
            if(file==null||!RelativePath(file.Path)||file.Size<0||file.Size>MaxFileBytes||!Hash(file.Sha256)||!paths.Add(file.Path))
                throw new InvalidOperationException("Invalid or duplicate snapshot file.");
            if(sizes.TryGetValue(file.Sha256,out var size)&&size!=file.Size)throw new InvalidOperationException("One content hash cannot declare different file sizes.");
            sizes[file.Sha256]=file.Size;total=checked(total+file.Size);
        }
        if(total>2L*1024*1024*1024*1024||BackupJson.Bytes(value).Length>MaxManifestBytes)
            throw new InvalidOperationException("Recording snapshot exceeds its storage limit.");
        foreach(var file in value.Files)
            for(var slash=file.Path.IndexOf('/');slash>=0;slash=file.Path.IndexOf('/',slash+1))
                if(paths.Contains(file.Path[..slash]))
                    throw new InvalidOperationException("A snapshot file cannot also be a directory.");
        if(!value.Files.Select(f=>f.Path).SequenceEqual(value.Files.Select(f=>f.Path).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Snapshot files must be ordered by path.");
    }
    public static bool RelativePath(string? value)=>value is {Length:>0 and <=1024}&&!System.IO.Path.IsPathRooted(value)
        &&!value.Contains('\\')&&!value.Contains(':')&&!value.Any(char.IsControl)
        &&value.Split('/').All(part=>part.Length>0&&part is not ("." or ".."));
    public static string FilePath(string root,string relative)
    {
        if(!RelativePath(relative))throw new InvalidOperationException("Invalid snapshot path.");
        var path=System.IO.Path.GetFullPath(System.IO.Path.Combine(root,relative));
        if(!path.StartsWith(System.IO.Path.GetFullPath(root)+System.IO.Path.DirectorySeparatorChar,StringComparison.Ordinal))
            throw new InvalidOperationException("Snapshot path leaves its storage directory.");
        return path;
    }
    public static void Plain(string path)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Snapshot files and directories cannot be symbolic links.");
    }
    public static async Task<bool> VerifyFile(string path,BackupFile file,CancellationToken token)
    {
        if(!File.Exists(path))return false;Plain(path);
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,true);
        if(input.Length!=file.Size)return false;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token))==file.Sha256;
    }
    public static void Write<T>(string path,T value,bool secret=false)
    {
        BackupDurability.EnsureDirectory(System.IO.Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
        if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        using(var file=new FileStream(temporary,options))
        {file.Write(BackupJson.Bytes(value));file.Flush(flushToDisk:true);}
        if(OperatingSystem.IsLinux())File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Move(temporary,path,true);BackupDurability.Directory(System.IO.Path.GetDirectoryName(path)!);
    }
    public static bool Token(string? value)=>value is {Length:>=32 and <=512}&&!value.Any(char.IsWhiteSpace)&&!value.Any(char.IsControl);
}
