using System.Text.Json;
using System.Text.RegularExpressions;
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
            return operation with {Stage="Complete",Message="Installation completed. Reboot from the installed disk.",Progress=new(5,"Complete"),Updated=DateTimeOffset.UtcNow};
        var progress=phase switch {
            "clock"=>"Checking network time and saving UTC to the hardware clock before disk erasure…",
            "anaconda"=>"Anaconda is installing the approved disk",
            "post"=>"Configuring the installed system…",
            _=>operation.Message
        };
        var steps=phase.Length==0?operation.Progress:Progress(phase,Tail(Path.Combine(logDirectory,"packaging.log")));
        if(phase=="anaconda"&&steps?.CompletedSteps<=2&&NativeDownloadProgress(Path.Combine(run,"install-download.json")) is {} nativeProgress)
            steps=new(2,"Download OS image",nativeProgress);
        if(steps!=null&&operation.Progress?.CompletedSteps>steps.CompletedSteps)steps=operation.Progress;
        if(phase is "anaconda" or "post"&&AnacondaOutput(Path.Combine(run,"anaconda-output.log")) is {} output)
            progress+="\n\nAnaconda:\n"+output;
        return progress==operation.Message&&steps==operation.Progress?operation:operation with{Message=progress,Progress=steps,Updated=DateTimeOffset.UtcNow};
    }
    public static InstallationProgress Progress(string phase,string logs)=>phase switch {
        "clock"=>new(0,"Synchronize system and hardware clocks"),
        "post"=>new(4,"Configure installed system and verify boot files"),
        "anaconda" when logs.Contains("Deploying container image",StringComparison.OrdinalIgnoreCase)=>new(3,"Deploy OS and bootloader"),
        "anaconda" when logs.Contains("layers needed:",StringComparison.OrdinalIgnoreCase)=>new(2,"Download OS image",DownloadDetail(logs)),
        "anaconda"=>new(1,"Prepare approved disk"),
        _=>new(0,"Preparing installation")
    };
    static string DownloadDetail(string logs)
    {
        var matches=Regex.Matches(logs,@"layers needed:\s*(\d+)(?:\s*\((\d+(?:\.\d+)?\s*[KMGTPE]?i?B)\))?",RegexOptions.IgnoreCase);
        if(matches.Count==0)return "Downloading the OS image…";
        var match=matches[^1];
        return "Downloading "+match.Groups[1].Value+" image layers"+(match.Groups[2].Success?" ("+Regex.Replace(match.Groups[2].Value,@"\s+"," ")+")":"")+".";
    }
    static string? NativeDownloadProgress(string path)
    {
        try
        {
            using var progress=JsonDocument.Parse(Read(path,4096));
            if(progress.RootElement.ValueKind!=JsonValueKind.Object)return null;
            string? Line(string name)=>progress.RootElement.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is {Length:>0 and <=512} text&&!text.Any(char.IsControl)?text:null;
            var layers=Line("layerProgress");
            if(layers==null||!layers.StartsWith("Fetching layers ",StringComparison.Ordinal))return null;
            var counts=Regex.Match(layers,@"\b(\d+)/(\d+)\b");
            if(!counts.Success||!int.TryParse(counts.Groups[1].Value,out var completed)||!int.TryParse(counts.Groups[2].Value,out var total)||total is <=0 or >1000000||completed>total)return null;
            var bytes=Line("byteProgress");
            if(progress.RootElement.TryGetProperty("byteProgress",out _)&&(bytes==null||!bytes.StartsWith("└ Fetching ",StringComparison.Ordinal)))return null;
            return Redaction.Logs(layers+(completed<total&&bytes!=null?"\n"+bytes:""));
        }
        catch(JsonException){return null;}
    }
    static string? AnacondaOutput(string path)
    {
        var text=Regex.Replace(Tail(path),@"\x1b\][^\x07]*(?:\x07|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~]","");
        text=new string(text.Where(c=>!char.IsControl(c)||c=='\n'||c=='\r'||c=='\t').ToArray());
        var recent=new List<string>();
        foreach(var line in Redaction.Logs(text).Split(['\n','\r']).Select(l=>l.Trim()).Where(l=>l.Length>0))
            if(recent.Count==0||recent[^1]!=line)recent.Add(line);
        var lines=recent.TakeLast(3).Select(l=>l.Length>160?l[..160]+"…":l).ToArray();
        return lines.Length>0?string.Join('\n',lines):null;
    }
    static string Tail(string path)
    {
        try{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);file.Seek(Math.Max(0,file.Length-65536),SeekOrigin.Begin);using var reader=new StreamReader(file);return reader.ReadToEnd();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return "";}
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
