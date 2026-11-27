namespace Xur.Control;
public static class StorageFiles
{
    public static string Url(string path)=>"/files?tab=storage&location="+Uri.EscapeDataString(path);
}
