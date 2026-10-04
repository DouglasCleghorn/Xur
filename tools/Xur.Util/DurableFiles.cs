using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Xur.Util;

public class DurableFiles
{
    public virtual void SyncDirectory(string path) => Linux.SyncDirectory(path);

    public virtual void Write(string path, byte[] content, uint mode = 0x180,
        uint? uid = null, uint? gid = null, Action<Linux.Metadata>? published = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".xur-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = (UnixFileMode)0x180
            }))
            {
                Linux.SetMetadata(stream.SafeFileHandle, mode, uid, gid);
                stream.Write(content);
                stream.Flush(flushToDisk: true);
                var metadata = Linux.Inspect(stream.SafeFileHandle);
                File.Move(temporary, path, overwrite: true);
                published?.Invoke(metadata);
            }
            SyncDirectory(directory);
        }
        finally { File.Delete(temporary); }
    }

    public void WriteJson(string path, JsonNode? value) =>
        Write(path, Encoding.UTF8.GetBytes((value?.ToJsonString() ?? "null") + "\n"));

    public static JsonNode? ReadJson(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    public static JsonObject ReadObject(string path) => ReadJson(path) as JsonObject ?? new JsonObject();
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
