using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Xur.Robot.Backups;

// Flush file contents plus directory entries before acknowledging verified
// copies. Atomic rename alone does not make a receipt durable across power loss.
public static partial class BackupDurability
{
    [LibraryImport("libc",EntryPoint="open",StringMarshalling=StringMarshalling.Utf8,SetLastError=true)]
    private static partial int Open(string path,int flags);
    [LibraryImport("libc",EntryPoint="fsync",SetLastError=true)]private static partial int Sync(int descriptor);
    [LibraryImport("libc",EntryPoint="close",SetLastError=true)]private static partial int Close(int descriptor);
    public static void Directory(string path)
    {
        if(!OperatingSystem.IsLinux())return;
        var descriptor=Open(path,0x10000); // O_RDONLY | O_DIRECTORY
        if(descriptor<0)throw new IOException("Could not open backup directory for durable storage.",new Win32Exception(Marshal.GetLastPInvokeError()));
        try{if(Sync(descriptor)!=0)throw new IOException("Could not flush backup directory metadata.",new Win32Exception(Marshal.GetLastPInvokeError()));}
        finally{Close(descriptor);}
    }
    public static void EnsureDirectory(string path)
    {
        var pending=new Stack<string>();var current=Path.GetFullPath(path);
        while(!System.IO.Directory.Exists(current))
        {pending.Push(current);current=Path.GetDirectoryName(current)??throw new IOException("Backup directory has no existing parent.");}
        System.IO.Directory.CreateDirectory(path);
        while(pending.TryPop(out var created)){Directory(Path.GetDirectoryName(created)!);Directory(created);}
    }
    public static void Tree(string path)
    {
        foreach(var directory in System.IO.Directory.GetDirectories(path))Tree(directory);
        Directory(path);
    }
    public static void MoveDirectory(string source,string destination)
    {
        Tree(source);EnsureDirectory(Path.GetDirectoryName(destination)!);System.IO.Directory.Move(source,destination);
        Directory(Path.GetDirectoryName(source)!);Directory(Path.GetDirectoryName(destination)!);
    }
}
