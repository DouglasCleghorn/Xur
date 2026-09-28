using Xur.Domain;
namespace Xur.Agent;

public static class InstallationDiagnostics
{
    public static Operation Observe(Operation operation,string run,string serviceState,string logDirectory="/tmp")
    {
        if(operation.Stage!="Installing")return operation;
        var failed=File.Exists(Path.Combine(run,"install-failed")) || serviceState.Split('\n').Contains("ActiveState=failed");
        var phase=Read(Path.Combine(run,"install-phase"),128).Trim();
        if(failed)
        {
            var message=phase switch {
                "clock"=>"Time synchronization failed before disk erasure.",
                "source"=>"Could not resolve the OS download source before disk installation.",
                "anaconda"=>"Anaconda failed while installing the approved disk.",
                "post"=>"Configuration of the installed system failed.",
                _=>"Installation failed."
            };
            var detail=Read(Path.Combine(run,"install-error"),1024).Trim();
            if(detail.Length>0)message=Redaction.Logs(detail);
            else if(DownloadFailure(FileLogs(logDirectory,150)) is {} download)message=download;
            return operation with {Stage="Failed",Message=message+" Open Installation logs for details. No automatic retry.",Updated=DateTimeOffset.UtcNow};
        }
        if(File.Exists(Path.Combine(run,"install-complete")))
            return operation with {Stage="Complete",Message="Installation completed. Reboot from the installed disk.",Updated=DateTimeOffset.UtcNow};
        var progress=phase switch {
            "clock"=>"Checking network time and saving UTC to the hardware clock before disk erasure…",
            "source"=>"Checking the OS download source before disk erasure…",
            "anaconda"=>"Anaconda is installing the approved disk",
            "post"=>"Configuring the installed system…",
            _=>operation.Message
        };
        return progress==operation.Message?operation:operation with{Message=progress,Updated=DateTimeOffset.UtcNow};
    }
    static string Read(string path,int limit)
    {
        try{using var file=File.OpenText(path);var buffer=new char[limit];return new string(buffer,0,file.ReadBlock(buffer,0,limit));}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return "";}
    }
    public static string? DownloadFailure(string logs)
    {
        if(logs.Contains("certificate has expired or is not yet valid",StringComparison.OrdinalIgnoreCase))
        {
            var time=System.Text.RegularExpressions.Regex.Match(logs,@"current time ([0-9T:.+Z-]+) is (before|after) ([0-9T:.+Z-]+)");
            return "OS download blocked by TLS certificate validation: "+(time.Success?"system time "+time.Groups[1].Value+" is "+time.Groups[2].Value+" the certificate validity boundary "+time.Groups[3].Value+". ":"the certificate is outside its validity dates. ")+"Check network time and the hardware clock before retrying.";
        }
        if(logs.Contains("tls: failed to verify certificate",StringComparison.OrdinalIgnoreCase))return "OS download failed TLS certificate verification. Check system time and the network certificate chain; verification remains enabled.";
        return null;
    }

    public static string FileLogs(string directory="/tmp",int lines=100)
    {
        var output="";
        foreach(var name in new[]{"xur-post.log","anaconda.log","storage.log","program.log","packaging.log"})
        {
            var path=Path.Combine(directory,name);
            try
            {
                if(File.Exists(path))output+="\n=== "+name+" (last "+lines+" lines) ===\n"+string.Join('\n',File.ReadLines(path).TakeLast(lines))+"\n";
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException){output+="\n"+name+" unavailable: "+e.Message+"\n";}
        }
        return Redaction.Logs(output.Length>0?output:"\nNo Anaconda log files have been written yet. Check the installer service journal.\n");
    }

    public static async Task<string> Read(int lines=100)
    {
        var output=FileLogs(lines:lines)+"\n=== Installer service journal ===\n";
        try
        {
            var result=await Processes.Run("journalctl",["--boot","--no-pager","-n","100","-u","xur-install.service"],10);
            output+=result.Output;
            if(result.ExitCode!=0)output+="\nJournal read failed (exit "+result.ExitCode+").";
        }
        catch(Exception e) when(e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException){output+="Journal unavailable: "+e.Message;}
        return Redaction.Logs(output);
    }
}
