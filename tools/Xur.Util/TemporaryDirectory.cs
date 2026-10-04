namespace Xur.Util;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; }
    public TemporaryDirectory(string parent, string prefix)
    {
        Path = System.IO.Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
