using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Xur.IO;

/// <summary>Spawn with terminal stderr only; preserve stdin/stdout, argv and Unix exit/signal status.</summary>
[SupportedOSPlatform("linux")]
public static partial class TerminalProcess
{
    public static int Execute(string executable, string[] arguments)
    {
        using var argv = new Strings([executable,..arguments]);
        Exec(executable,argv.Pointer);
        throw Error("Could not execute " + executable);
    }
    public static int Run(string executable, string[] arguments, Action<string> feed)
    {
        var master = PosixOpenpt(2 | 0x100 | 0x80000);
        if (master < 0) return Execute(executable,arguments);
        using var terminal = new SafeFileHandle(master,true);
        var name = new byte[4096];
        if (Grantpt(terminal) != 0 || Unlockpt(terminal) != 0 || Ptsname(terminal,name,(nuint)name.Length) != 0) return Execute(executable,arguments);
        var slaveFd = Open(Encoding.UTF8.GetString(name.AsSpan(0,Array.IndexOf(name,(byte)0))),2 | 0x100 | 0x80000);
        if (slaveFd < 0) return Execute(executable,arguments);
        using var slave = new SafeFileHandle(slaveFd,true);
        var size = new Window { Rows=24, Columns=240 };
        if (Ioctl(slave,0x5414,ref size) < 0) return Execute(executable,arguments);
        var actions = new Actions(); Check(ActionsInit(ref actions));
        int pid;
        try
        {
            Check(ActionsDup(ref actions,slaveFd,2));
            using var argv = new Strings([executable,..arguments]);
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(e => e.Key.ToString() is not ("TERM" or "LC_ALL")).Select(e => e.Key+"="+e.Value).Concat(["TERM=xterm","LC_ALL=C"]).ToArray();
            using var env = new Strings(environment);
            Check(Spawn(out pid,executable,ref actions,0,argv.Pointer,env.Pointer));
        }
        finally { ActionsDestroy(ref actions); }
        slave.Dispose();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM,c=>{c.Cancel=true;Kill(pid,15);});
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT,c=>{c.Cancel=true;Kill(pid,2);});
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP,c=>{c.Cancel=true;Kill(pid,1);});
        try
        {
            var decoder=Encoding.UTF8.GetDecoder(); var bytes=new byte[8192];var characters=new char[8192];
            while(true)
            {
                var count=Read(terminal,bytes,(nuint)bytes.Length);
                if(count<0)
                {
                    var error=Marshal.GetLastPInvokeError();if(error==4)continue;if(error==5)break;
                    throw Error("Could not read terminal stderr");
                }
                if(count==0)break;
                var decoded=decoder.GetChars(bytes,0,(int)count,characters,0);feed(new string(characters,0,decoded));
            }
            var final=decoder.GetChars([],0,0,characters,0,flush:true);feed(new string(characters,0,final)+"\n");
        }
        catch { Kill(pid,15); Wait(pid); throw; }
        var status=Wait(pid);var signal=status&0x7f;
        if(signal==0)return (status>>8)&0xff;
        term.Dispose();interrupt.Dispose();hangup.Dispose();
        Signal(signal,0);Kill(Getpid(),signal);return 128+signal;
    }
    static int Wait(int pid)
    {
        while(true)
        { if(Waitpid(pid,out var status,0)>=0)return status;if(Marshal.GetLastPInvokeError()!=4)throw Error("Could not wait for child process"); }
    }
    static IOException Error(string message)=>new(message+": "+new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    static void Check(int error){if(error!=0)throw new IOException("Could not spawn terminal process: "+new Win32Exception(error).Message);}
    sealed class Strings : IDisposable
    {
        readonly nint[] values; public nint Pointer {get;}
        public Strings(string[] strings)
        {
            if(strings.Any(s=>s.Contains('\0')))throw new ArgumentException("Arguments cannot contain NUL.");
            values=strings.Select(Marshal.StringToCoTaskMemUTF8).ToArray();Pointer=Marshal.AllocHGlobal((values.Length+1)*IntPtr.Size);
            for(var index=0;index<values.Length;index++)Marshal.WriteIntPtr(Pointer,index*IntPtr.Size,values[index]);
            Marshal.WriteIntPtr(Pointer,values.Length*IntPtr.Size,0);
        }
        public void Dispose(){foreach(var value in values)Marshal.FreeCoTaskMem(value);Marshal.FreeHGlobal(Pointer);}
    }
    // glibc's public posix_spawn_file_actions_t layout on Linux x64/arm64.
    [StructLayout(LayoutKind.Sequential)] struct Actions { public int Allocated, Used; public nint Items; public long Reserved0,Reserved1,Reserved2,Reserved3,Reserved4,Reserved5,Reserved6,Reserved7; }
    [StructLayout(LayoutKind.Sequential)] struct Window {public ushort Rows,Columns,Xpixels,Ypixels;}
    [LibraryImport("libc",EntryPoint="posix_openpt",SetLastError=true)] private static partial int PosixOpenpt(int flags);
    [LibraryImport("libc",EntryPoint="grantpt",SetLastError=true)] private static partial int Grantpt(SafeFileHandle fd);
    [LibraryImport("libc",EntryPoint="unlockpt",SetLastError=true)] private static partial int Unlockpt(SafeFileHandle fd);
    [LibraryImport("libc",EntryPoint="ptsname_r")] private static partial int Ptsname(SafeFileHandle fd,[Out]byte[] name,nuint length);
    [LibraryImport("libc",EntryPoint="open",SetLastError=true,StringMarshalling=StringMarshalling.Utf8)] private static partial int Open(string path,int flags);
    [LibraryImport("libc",EntryPoint="ioctl",SetLastError=true)] private static partial int Ioctl(SafeFileHandle fd,nuint operation,ref Window size);
    [LibraryImport("libc",EntryPoint="posix_spawn_file_actions_init")] private static partial int ActionsInit(ref Actions actions);
    [LibraryImport("libc",EntryPoint="posix_spawn_file_actions_adddup2")] private static partial int ActionsDup(ref Actions actions,int fd,int target);
    [LibraryImport("libc",EntryPoint="posix_spawn_file_actions_destroy")] private static partial int ActionsDestroy(ref Actions actions);
    [LibraryImport("libc",EntryPoint="posix_spawn",StringMarshalling=StringMarshalling.Utf8)] private static partial int Spawn(out int pid,string executable,ref Actions actions,nint attributes,nint argv,nint env);
    [LibraryImport("libc",EntryPoint="read",SetLastError=true)] private static partial nint Read(SafeFileHandle fd,[Out]byte[] bytes,nuint length);
    [LibraryImport("libc",EntryPoint="waitpid",SetLastError=true)] private static partial int Waitpid(int pid,out int status,int options);
    [LibraryImport("libc",EntryPoint="kill",SetLastError=true)] private static partial int Kill(int pid,int signal);
    [LibraryImport("libc",EntryPoint="signal")] private static partial nint Signal(int signal,nint handler);
    [LibraryImport("libc",EntryPoint="getpid")] private static partial int Getpid();
    [LibraryImport("libc",EntryPoint="execv",SetLastError=true,StringMarshalling=StringMarshalling.Utf8)] private static partial int Exec(string executable,nint argv);
}
