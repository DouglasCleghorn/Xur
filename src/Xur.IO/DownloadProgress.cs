using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Xur.IO;

/// <summary>Bounded parsing of bootc terminal draws; reporting failures cannot fail installation.</summary>
[SupportedOSPlatform("linux")]
public sealed partial class DownloadProgress(string path, TextWriter errors)
{
    public string Pending {get;private set;}="";
    public bool Disabled {get;private set;}
    JsonObject? native; int completed=-1,total=-1;
    public void Feed(string text)
    {
        var pending=Pending+text;var start=0;
        for(var index=0;index<pending.Length;index++)
            if(pending[index] is '\r' or '\n'){Line(Ansi().Replace(pending[start..index],"").Trim());start=index+1;}
        Pending=pending[Math.Max(start,pending.Length-4096)..];
    }
    void Line(string line)
    {
        var layers=Layers().Match(line);
        if(layers.Success)
        {
            if(int.TryParse(layers.Groups[1].Value,out var done)&&int.TryParse(layers.Groups[2].Value,out var count)&&done>=0&&done<=count&&count is >0 and <=1000000)
            {
                if(completed!=done||total!=count){native=new();completed=done;total=count;}
                native!["layerProgress"]=line[..Math.Min(512,line.Length)];Write();
            }
            return;
        }
        if(Layer().IsMatch(line))
        {
            if(native is not null&&completed<total){native["byteProgress"]=line[..Math.Min(512,line.Length)];Write();}
            return;
        }
        if(line.Length!=0){errors.WriteLine(line);errors.Flush();}
    }
    void Write()
    {
        if(Disabled)return;
        var temporary=path+".tmp";
        try
        {
            File.WriteAllText(temporary,native!.ToJsonString()+"\n");File.SetUnixFileMode(temporary,(UnixFileMode)384);
            File.Move(temporary,path,overwrite:true);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        {Disabled=true;errors.WriteLine("Download counters could not be saved; stage progress remains available.");errors.Flush();}
        finally { try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){} }
    }
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]")] private static partial Regex Ansi();
    [GeneratedRegex(@"Fetching layers\s+.*?\b(\d+)/(\d+)\b")] private static partial Regex Layers();
    [GeneratedRegex(@"Fetching\s+.*?(\d+(?:\.\d+)?\s*(?:[KMGTPE]i?B|B))/(\d+(?:\.\d+)?\s*(?:[KMGTPE]i?B|B))\s+\((\d+(?:\.\d+)?\s*(?:[KMGTPE]i?B|B)/s)\)")] private static partial Regex Layer();
}
