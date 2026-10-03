namespace Xur.Domain;

public static class FileCleanup
{
    // Runtime directories disappear on reboot and may never have been created
    // when startup fails. Only absence is success; permission/I/O errors remain.
    public static void DeleteIfPresent(string path)
    {
        try { File.Delete(path); }
        catch(DirectoryNotFoundException) { }
        catch(FileNotFoundException) { }
    }
}
