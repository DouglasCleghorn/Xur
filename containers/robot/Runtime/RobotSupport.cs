using System.Diagnostics;
using System.Globalization;
using System.Numerics;
namespace Xur.Robot;
public static class RobotIdentifiers
{
    public static bool Valid(string? value)=>value!=null && System.Text.RegularExpressions.Regex.IsMatch(value,@"\A[a-z][a-z0-9-]{0,47}\z");
}
public static class RobotCameraDevices
{
    public static bool Valid(string? path)=>path!=null&&System.Text.RegularExpressions.Regex.IsMatch(path,
        @"\A/dev/v4l/by-(?:id|path)/[A-Za-z0-9._:+-]+-video-index0\z");
    static string Node(string path)=>File.ResolveLinkTarget(path,true)?.FullName??Path.GetFullPath(path);
    public static bool SameNode(string left,string right)
    {
        if(left==right)return true;
        try{return File.Exists(left)&&File.Exists(right)&&Node(left)==Node(right);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){return false;}
    }
    public static RobotDevice[] Read(string byId="/dev/v4l/by-id",string byPath="/dev/v4l/by-path")
    {
        var aliases=new List<(string Path,string Node,bool ByPath)>();
        foreach(var (directory,isByPath) in new[]{(byId,false),(byPath,true)})
        {
            if(!Directory.Exists(directory))continue;
            string[] paths;
            try{paths=Directory.GetFiles(directory,"*-video-index0");}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){continue;}
            foreach(var path in paths)
            {
                try
                {
                    var node=Node(path);
                    if(File.Exists(node))aliases.Add((path,node,isByPath));
                }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException){}
            }
        }
        // Different capture interfaces remain separate even when their generic
        // by-id names collide. Prefer interface-specific by-path aliases for
        // a shared node; do not guess RGB/IR, camera roles or supported formats.
        return aliases.GroupBy(a=>a.Node,StringComparer.Ordinal)
            .Select(group=>group.OrderByDescending(a=>a.ByPath).ThenBy(a=>a.Path,StringComparer.Ordinal).First().Path)
            .Order(StringComparer.Ordinal).Select(path=>new RobotDevice(path,Path.GetFileName(path))).ToArray();
    }
}
public static class ControllerInventory
{
    public static bool IsGamepad(string path)
    {
        try
        {
            var words=File.ReadAllText("/sys/class/input/"+Path.GetFileName(path)+"/device/capabilities/key").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            var keys=BigInteger.Parse("0"+string.Concat(words.Select(w=>w.PadLeft(16,'0'))),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture);
            return (keys & (BigInteger.One<<304))!=0; // Linux BTN_GAMEPAD.
        }
        catch(Exception error) when(error is IOException or FormatException or UnauthorizedAccessException){return false;}
    }
}
public record ProcessResult(int ExitCode,string Output);
public static class Processes
{
    public static async Task<ProcessResult> Run(string executable,IEnumerable<string> arguments,int seconds=30,CancellationToken cancellation=default)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var value in arguments)info.ArgumentList.Add(value);
        using var process=Process.Start(info)??throw new IOException("Could not start the local robot tool.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var output=process.StandardOutput.ReadToEndAsync(deadline.Token);var error=process.StandardError.ReadToEndAsync(deadline.Token);
        try{await process.WaitForExitAsync(deadline.Token);return new(process.ExitCode,await output+await error);}
        catch{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();try{await Task.WhenAll(output,error);}catch{}throw;}
    }
}
