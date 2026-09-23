using Xur.Domain;
namespace Xur.Agent;

public static class InstallationDiagnostics
{
    public static Operation Observe(Operation operation,string run,string serviceState)
    {
        if(operation.Stage!="Installing")return operation;
        var failed=File.Exists(Path.Combine(run,"install-failed")) || serviceState.Split('\n').Contains("ActiveState=failed");
        if(failed)
        {
            var path=Path.Combine(run,"install-phase");
            var phase=File.Exists(path)?File.ReadAllText(path).Trim():"";
            var message=phase switch {
                "source"=>"Could not resolve the OS download source before disk installation.",
                "anaconda"=>"Anaconda failed while installing the approved disk.",
                "post"=>"Configuration of the installed system failed.",
                _=>"Installation failed."
            };
            return operation with {Stage="Failed",Message=message+" Open Installation logs for details. No automatic retry.",Updated=DateTimeOffset.UtcNow};
        }
        if(File.Exists(Path.Combine(run,"install-complete")))
            return operation with {Stage="Complete",Message="Installation completed. Reboot from the installed disk.",Updated=DateTimeOffset.UtcNow};
        return operation;
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
