using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Xur.Robot;

public record RobotCameraCapabilityReport(DateTimeOffset ObservedAt, string? SelectedHeadCamera,
    string? SelectedHandCamera, RobotCameraCapabilityDevice[] Devices, string[] Problems,
    bool CameraSettingsChanged = false, bool MotorCommandsIssued = false);
public record RobotCameraCapabilityDevice(string Path, string Name, string[] SelectedRoles, string State,
    RobotCameraControl[] Controls, RobotCameraFormat[] Formats, RobotCameraDriver Driver,
    RobotCameraCapabilityQuery[] Queries);
public record RobotCameraCapabilityQuery(string Name, string Argument, string State, int? ExitCode,
    string? Problem, string Raw, bool OutputTruncated = false);
public record RobotCameraControl(string Name, string Id, string Type, string? Current,
    long? Minimum, long? Maximum, long? Step, long? Default, string[] Flags,
    RobotCameraMenuEntry[] Menu, Dictionary<string,string> Attributes, bool PayloadUninterpreted, string Raw);
public record RobotCameraMenuEntry(long Index, string Label);
public record RobotCameraFormat(int Index, string PixelFormat, string Description, RobotCameraFrameSize[] Sizes);
public record RobotCameraFrameSize(string Kind, int? Width, int? Height, RobotCameraFrameInterval[] Intervals, string Raw);
public record RobotCameraFrameInterval(string Kind, double? Seconds, double? FramesPerSecond, string Raw);
public record RobotCameraDriver(string? Name, string? Card, string? Bus, string? Version, RobotCameraCurrentMode? CurrentMode);
public record RobotCameraCurrentMode(int? Width, int? Height, string? PixelFormat, double? FramesPerSecond);

// Fixed read-only v4l2-ctl queries. No HTTP request can supply an executable,
// option, control name/value, device path or device role to this component.
public sealed class RobotCameraCapabilities
{
    const string Executable = "/usr/bin/v4l2-ctl";
    const int RawLimit = 256 * 1024;
    static readonly (string Name,string Argument)[] AllowedQueries =
        [("controls","--list-ctrls-menus"),("formats","--list-formats-ext"),("device","--all")];
    readonly Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>> run;
    public RobotCameraCapabilities(Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>>? run = null)
        => this.run = run ?? RunBounded;

    static async Task<ProcessResult> RunBounded(string executable,IEnumerable<string> arguments,int seconds,CancellationToken token)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new IOException("Could not start v4l2-ctl.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        // Drain both pipes, retaining only enough to identify/report truncation.
        // Large payload controls must not allocate an unbounded driver transcript.
        var output=ReadBounded(process.StandardOutput,deadline.Token);var error=ReadBounded(process.StandardError,deadline.Token);
        try{await process.WaitForExitAsync(deadline.Token);return new(process.ExitCode,await output+await error);}
        catch
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync();try{await Task.WhenAll(output,error);}catch{}throw;
        }
    }
    static async Task<string> ReadBounded(StreamReader reader,CancellationToken token)
    {
        var text=new StringBuilder();var buffer=new char[4096];int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),token))!=0)
        {
            var retain=Math.Min(count,RawLimit+1-text.Length);
            if(retain>0)text.Append(buffer,0,retain);
        }
        return text.ToString();
    }

    public async Task<RobotCameraCapabilityReport> Read(RobotDevice[] inventory, RoboticsConfiguration? configuration, CancellationToken token)
    {
        var devices = new List<RobotCameraCapabilityDevice>(); var problems = new List<string>();
        var cameras = inventory.GroupBy(d=>d.Path,StringComparer.Ordinal).Select(g=>g.First()).ToArray();
        if (cameras.Length > 16) problems.Add("Only the first 16 discovered capture interfaces were inspected.");
        foreach (var camera in cameras.Take(16))
        {
            token.ThrowIfCancellationRequested();
            if (!RobotCameraDevices.Valid(camera.Path))
                throw new InvalidOperationException("Camera capability inspection requires discovered v4l/by-id or v4l/by-path capture interfaces.");
            var queries = new List<RobotCameraCapabilityQuery>();
            foreach (var (name,argument) in AllowedQueries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await run(Executable,["--device",camera.Path,argument],5,token);
                    var truncated = result.Output.Length > RawLimit;
                    queries.Add(new(name,argument,result.ExitCode!=0?"failed":truncated?"partial":"available",result.ExitCode,
                        result.ExitCode!=0?"Driver query failed; see raw output.":truncated?"Driver output exceeded the report limit and is truncated.":null,
                        truncated?result.Output[..RawLimit]:result.Output,truncated));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {queries.Add(new(name,argument,"unavailable",null,"Read-only driver query timed out.",""));}
                catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
                {queries.Add(new(name,argument,"unavailable",null,"Read-only v4l2-ctl query unavailable: "+Xur.Domain.Redaction.Logs(error.Message),""));}
            }
            var results = queries.ToArray();
            var roles = new List<string>();
            if(configuration is not null)
            {
                if(RobotCameraDevices.SameNode(configuration.HeadCamera,camera.Path))roles.Add("head");
                if(RobotCameraDevices.SameNode(configuration.HandCamera,camera.Path))roles.Add("hand");
            }
            devices.Add(new(camera.Path,camera.Name,roles.ToArray(),results.All(q=>q.State=="available")?"available":results.All(q=>q.State is "unavailable" or "failed")?"unavailable":"partial",
                ParseControls(results[0].Raw),ParseFormats(results[1].Raw),ParseDriver(results[2].Raw),results));
        }
        if (cameras.Length==0) problems.Add("No capture camera interfaces are discovered in this container.");
        return new(DateTimeOffset.UtcNow,configuration?.HeadCamera,configuration?.HandCamera,devices.ToArray(),problems.ToArray());
    }

    static readonly Regex ControlHeader = new(@"^\s*(?<name>\S+)\s+(?<id>0x[0-9a-fA-F]+)\s+\((?<type>[^)]+)\)\s*:\s*(?<fields>.*)$",RegexOptions.CultureInvariant);
    static readonly Regex Attribute = new(@"(?<key>[a-z][a-z_0-9]*)=(?<value>.*?)(?=\s+[a-z][a-z_0-9]*=|$)",RegexOptions.CultureInvariant);
    static readonly Regex Menu = new(@"^\s+(?<index>-?\d+):\s*(?<label>.*)$",RegexOptions.CultureInvariant);
    public static RobotCameraControl[] ParseControls(string raw)
    {
        var controls = new List<RobotCameraControl>(); var menu = new List<RobotCameraMenuEntry>();
        Match? header = null; var lines = new List<string>();
        void Finish()
        {
            if(header is null)return;
            var attributes = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(Match field in Attribute.Matches(header.Groups["fields"].Value))attributes[field.Groups["key"].Value]=field.Groups["value"].Value.Trim();
            var type = header.Groups["type"].Value.Trim();
            var flags = attributes.GetValueOrDefault("flags")?.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)??[];
            controls.Add(new(header.Groups["name"].Value,header.Groups["id"].Value,type,attributes.GetValueOrDefault("value"),
                Integer(attributes.GetValueOrDefault("min")),Integer(attributes.GetValueOrDefault("max")),Integer(attributes.GetValueOrDefault("step")),Integer(attributes.GetValueOrDefault("default")),
                flags,menu.ToArray(),attributes,flags.Contains("has-payload",StringComparer.Ordinal)||type is not ("int" or "int64" or "bool" or "menu" or "intmenu" or "bitmask" or "button"),string.Join('\n',lines)));
            header=null; lines.Clear(); menu.Clear();
        }
        foreach(var line in raw.Split('\n'))
        {
            var candidate = ControlHeader.Match(line);
            if(candidate.Success){Finish();header=candidate;lines.Add(line.TrimEnd('\r'));continue;}
            if(header is null)continue;
            var entry=Menu.Match(line);
            if(entry.Success&&long.TryParse(entry.Groups["index"].Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var index))
            {menu.Add(new(index,entry.Groups["label"].Value.TrimEnd('\r')));lines.Add(line.TrimEnd('\r'));}
            else if(!string.IsNullOrWhiteSpace(line))Finish();
        }
        Finish();return controls.ToArray();
    }
    static long? Integer(string? value)
    {
        if(value is null)return null;
        return value.StartsWith("0x",StringComparison.OrdinalIgnoreCase)
            ?long.TryParse(value.AsSpan(2),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out var hex)?hex:null
            :long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var number)?number:null;
    }

    static readonly Regex FormatHeader = new(@"^\s*\[(?<index>\d+)\]:\s*'(?<fourcc>[^']+)'\s*\((?<description>.*)\)\s*$",RegexOptions.CultureInvariant);
    static readonly Regex Size = new(@"^\s*Size:\s*(?<kind>\S+)\s*(?<value>.*)$",RegexOptions.CultureInvariant);
    static readonly Regex DiscreteSize = new(@"^(?<width>\d+)x(?<height>\d+)$",RegexOptions.CultureInvariant);
    static readonly Regex Interval = new(@"^\s*Interval:\s*(?<kind>\S+)\s*(?<value>.*)$",RegexOptions.CultureInvariant);
    static readonly Regex DiscreteInterval = new(@"^(?<seconds>[0-9.]+)s\s*\((?<fps>[0-9.]+) fps\)$",RegexOptions.CultureInvariant);
    public static RobotCameraFormat[] ParseFormats(string raw)
    {
        var formats=new List<RobotCameraFormat>();var sizes=new List<RobotCameraFrameSize>();var intervals=new List<RobotCameraFrameInterval>();
        Match? format=null;Match? size=null;
        void FinishSize()
        {
            if(size is null)return;
            var discrete=DiscreteSize.Match(size.Groups["value"].Value.Trim());
            var parsed=size.Groups["kind"].Value=="Discrete"&&discrete.Success;
            sizes.Add(new(size.Groups["kind"].Value,parsed?Int(discrete.Groups["width"].Value):null,parsed?Int(discrete.Groups["height"].Value):null,intervals.ToArray(),size.Value.Trim()));
            size=null;intervals.Clear();
        }
        void FinishFormat()
        {
            FinishSize();if(format is null)return;
            if(int.TryParse(format.Groups["index"].Value,NumberStyles.None,CultureInfo.InvariantCulture,out var index))formats.Add(new(index,format.Groups["fourcc"].Value,format.Groups["description"].Value,sizes.ToArray()));
            format=null;sizes.Clear();
        }
        foreach(var source in raw.Split('\n'))
        {
            var line=source.TrimEnd('\r');var candidate=FormatHeader.Match(line);
            if(candidate.Success){FinishFormat();format=candidate;continue;}
            if(format is null)continue;
            var candidateSize=Size.Match(line);
            if(candidateSize.Success){FinishSize();size=candidateSize;continue;}
            if(size is null)continue;
            var interval=Interval.Match(line);if(!interval.Success)continue;
            var discrete=DiscreteInterval.Match(interval.Groups["value"].Value.Trim());
            var parsed=interval.Groups["kind"].Value=="Discrete"&&discrete.Success;
            intervals.Add(new(interval.Groups["kind"].Value,parsed?Number(discrete.Groups["seconds"].Value):null,parsed?Number(discrete.Groups["fps"].Value):null,line.Trim()));
        }
        FinishFormat();return formats.ToArray();
    }
    static int? Int(string value)=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var number)&&number>0?number:null;
    static double? Number(string value)=>double.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number)&&number>0?number:null;

    public static RobotCameraDriver ParseDriver(string raw)
    {
        static string? Field(string section,string name)
        {var match=Regex.Match(section,@"(?m)^\s*"+Regex.Escape(name)+@"\s*:\s*(?<value>[^\r\n]*)",RegexOptions.CultureInvariant);return match.Success?match.Groups["value"].Value:null;}
        static string Section(string text,string header)
        {var match=Regex.Match(text,@"(?ms)^"+Regex.Escape(header)+@":\s*\r?\n(?<body>.*?)(?=^\S|\z)",RegexOptions.CultureInvariant);return match.Success?match.Groups["body"].Value:"";}
        var driver=Section(raw,"Driver Info");var mode=Section(raw,"Format Video Capture");
        if(mode=="")mode=Section(raw,"Format Video Capture Multiplanar");
        var dimensions=Field(mode,"Width/Height")?.Split('/');var pixel=Field(mode,"Pixel Format");
        var pixelMatch=Regex.Match(pixel??"",@"^'(?<fourcc>[^']+)'",RegexOptions.CultureInvariant);
        var fps=Field(Section(raw,"Streaming Parameters Video Capture"),"Frames per second")?.Split(' ')[0];
        return new(Field(driver,"Driver name"),Field(driver,"Card type"),Field(driver,"Bus info"),Field(driver,"Driver version"),mode==""?null:
            new(dimensions?.Length==2?Int(dimensions[0].Trim()):null,dimensions?.Length==2?Int(dimensions[1].Trim()):null,pixelMatch.Success?pixelMatch.Groups["fourcc"].Value:null,fps is null?null:Number(fps)));
    }
}
