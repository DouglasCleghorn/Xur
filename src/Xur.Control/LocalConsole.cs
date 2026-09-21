using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Xur.Domain;
using Spectre.Console;
using Microsoft.Win32.SafeHandles;

namespace Xur.Control;

// Spectre renders the bounded layout; the VT transport owns cursor and device routing.
public static class LocalConsole
{
    static readonly object Sync = new();
    static readonly Dictionary<string,string> LastFrames = [];
    static readonly Dictionary<string,FileStream> Devices = [];
    static Appliance? appliance;
    static Bootstrap? bootstrap;
    static string title = "Status and login", body = "", logs = "Waiting for logs", qr = "";
    static string view = "status";
    static bool serialLogs;
    static int page;
    static int selection;
    static ConsoleScreen? maintenance;
    static string textBuffer="";static bool replaceText;
    public static bool EditingText {get{lock(Sync)return view=="maintenance" && maintenance?.InputValue!=null;}}
    public static string TextValue {get{lock(Sync)return textBuffer;}}
    static string DisplayText=>maintenance?.Secret==true?new string('*',textBuffer.Length):textBuffer;
    public static void ClearText(){lock(Sync){textBuffer="";replaceText=true;}}
    public static void EditText(char character)
    {
        lock(Sync)
        {
            if(!EditingText)return;
            if(character is '\b' or '\x7f'){textBuffer=replaceText?"":textBuffer.Length>0?textBuffer[..^1]:"";replaceText=false;}
            else if(character is >= ' ' and <= '~' && textBuffer.Length<(maintenance?.Secret==true?64:1024)){textBuffer=(replaceText?"":textBuffer)+character;replaceText=false;}
            body=Clean(maintenance!.Body)+"\n> "+DisplayText;Render();
        }
    }
    public static string[] RootOptions(bool installer=false) => installer
        ? ["Status and login", "Tailscale QR", "Network settings", "Hardware", "Logs", "Power", "Server name"]
        : ["Status and login", "Tailscale QR", "Network settings", "Hardware", "Logs", "Updates", "Power", "Server name"];
    static string[] Options => RootOptions(appliance?.Installer==true);
    static string[] CurrentOptions => view=="maintenance" ? maintenance!.Options.Select(o=>o.Display).ToArray() : Options;
    public static bool ViewingMaintenance { get {lock(Sync)return view=="maintenance";} }
    static bool Plain => Environment.GetEnvironmentVariable("XUR_CONSOLE") == "stdio";
    public static string Menu(bool installer=false) => "\n"+string.Join('\n',RootOptions(installer).Select((label,i)=>$"{i+1}. {label}"))+"\n0. Exit\nSelection: ";
    public static char RootKey(int index,bool installer=false) => (installer ? "12j45wm" : "12j45uwm")[index];

    public static async Task Start(Appliance app, Bootstrap auth)
    {
        appliance=app; bootstrap=auth;
        if(!Plain)
        {
            // Linux VTs can reset termios after their last descriptor closes.
            // Hold each device open before setting no-echo, through shutdown.
            foreach(var path in new[]{"/dev/tty3","/dev/ttyS0","/dev/tty2"})
                try { Devices[path]=OpenDevice(path,FileAccess.Write); } catch { }
            foreach(var path in new[]{"/dev/tty3","/dev/ttyS0","/dev/tty2"})
                if(Devices.TryGetValue(path,out var device))ConfigureInput(device);
            ProtectConsole();
        }
        Status(app,auth);
        if(!Plain)try {
            // An application restart must not switch away from a running desktop.
            var stations=await Processes.Run("systemctl",["list-units","--state=active","--no-legend","--plain","xur-station-*.service"],3);
            if(stations.ExitCode!=0 || string.IsNullOrWhiteSpace(stations.Output))await Processes.Run("chvt",["3"],3);
        } catch { }
    }
    static string StatusText() => appliance==null || bootstrap==null ? "Starting" :
        "Server: "+Environment.MachineName+"\n\n"+
        (bootstrap.AccountConfigured ? "Sign in:\n  User: "+bootstrap.Username+"\n  Use your password in the web manager." : "Initial login:\n  User: xur\n  Access code: "+bootstrap.DisplayCode)+
        "\n\nWeb manager:\n"+WebAddresses(appliance.Urls())+
        "\n\nTailscale: "+appliance.TailscaleState+(appliance.TailUrl.Length>0?"\n  "+appliance.TailUrl:"")+"\n"+appliance.TailServeStatus;
    public static string WebAddresses(string[] urls) => urls.Length==0 ? "  Waiting for network addresses (DHCP / Tailscale)..." : string.Join('\n',urls.Select(u=>"  "+u));
    static string lastQrUrl="";static string[] serveQr=[];
    static string[] ServeQr()
    {
        var url=appliance?.TailServeReady==true&&bootstrap!=null?ConsoleQr.LoginUrl(appliance.TailUrl,bootstrap.AccountConfigured,bootstrap.DisplayCode):"";
        if(url!=lastQrUrl){lastQrUrl=url;serveQr=url.Length==0?[]:ConsoleQr.Rows(url);}return serveQr;
    }
    static bool ConnectedQr=>appliance?.TailServeReady==true&&appliance.TailUrl.Length>0;
    static string QrText()=>ConnectedQr?string.Join('\n',ServeQr()):appliance?.TailscaleState=="Running"?appliance.TailServeStatus+"\nUse the LAN HTTPS address while this reconnects.":qr.Length==0?"Starting real Tailscale enrollment...":qr;
    static string NetworkText() => appliance==null ? "Starting" : string.Join("\n\n",appliance.Network().Select(n=>n.Name+" · "+n.State+"\n"+(n.Addresses.Length==0?"  Waiting for an address":string.Join('\n',n.Addresses.Select(a=>"  "+a)))))+"\n\nTailscale: "+appliance.TailscaleState+"\n"+appliance.TailUrl;
    public static void OpenNetwork()
    { lock(Sync) {view="network";title="IP addresses · live";body=NetworkText();serialLogs=false;page=0;Render();} }
    public static void Status(Appliance app, Bootstrap auth)
    {
        lock(Sync) { appliance=app; bootstrap=auth; view="status"; title="Status and login"; serialLogs=false; page=0; selection=0;
            body=StatusText(); Render(); }
    }
    public static void Refresh()
    { lock(Sync) {
        if(!Plain)ProtectConsole();
        foreach(var path in new[]{"/dev/tty3","/dev/ttyS0","/dev/tty2"})
            if(Devices.TryGetValue(path,out var device))ConfigureInput(device);
        if(view=="status") body=StatusText();
        if(view=="qr"&&ConnectedQr)title="Scan to open Xur";
        if(view=="network") body=NetworkText();
        Render();
    } }
    static void ProtectConsole()
    {
        // printk defaults to the foreground VT, regardless of which tty the
        // application opens. Pin it to the boot console, including when an
        // installer tool raises the console log level again. Do not disable
        // auditing or discard the kernel/journal logs.
        if(Devices.TryGetValue("/dev/tty3",out var terminal))
            ConsoleIoctl(terminal.SafeFileHandle.DangerousGetHandle().ToInt32(),0x541c,[11,1]); // TIOCL_SETKMSGREDIRECT -> VT1
        KernelLog(6,IntPtr.Zero,0); // SYSLOG_ACTION_CONSOLE_OFF; Anaconda can reset it.
    }
    public static char? Navigate(ConsoleKeyAction action)
    {
        lock(Sync)
        {
            if(action==ConsoleKeyAction.Enter)
            {
                if(view=="qr" || serialLogs)return '0';
                if(view=="maintenance")return maintenance!.Options[selection].Enabled ? maintenance.Options[selection].Key : null;
                return RootKey(selection,appliance?.Installer==true);
            }
            if(action==ConsoleKeyAction.Back)return '0';
            if(view=="qr")return null; // Its single selectable action is Back to menu.
            if(action==ConsoleKeyAction.PageDown)return 'n';
            if(action==ConsoleKeyAction.PageUp)return 'p';
            if(action==ConsoleKeyAction.Up)selection=(selection+CurrentOptions.Length-1)%CurrentOptions.Length;
            else if(action==ConsoleKeyAction.Down)selection=(selection+1)%CurrentOptions.Length;
            else if(action>=ConsoleKeyAction.One && action<=ConsoleKeyAction.Nine)
            {
                var index=action-ConsoleKeyAction.One;
                if(index>=CurrentOptions.Length)return null;
                selection=index;
            }
            Render();return null;
        }
    }
    public static void OpenMaintenance(ConsoleScreen screen,bool refreshOnly=false)
    {
        lock(Sync)
        {
            if(refreshOnly && (view!="maintenance" || maintenance?.Id!=screen.Id))return;
            if(view!="maintenance" || maintenance?.Id!=screen.Id){selection=0;page=0;textBuffer=screen.InputValue??"";replaceText=true;}
            else if(maintenance!=null)
            {
                var key=maintenance.Options[Math.Min(selection,maintenance.Options.Length-1)].Key;
                var index=Array.FindIndex(screen.Options,o=>o.Key==key);
                selection=index<0?0:index;
            }
            maintenance=screen;view="maintenance";title=screen.Title;body=Clean(screen.Body)+(screen.InputValue!=null?"\n> "+DisplayText:"");serialLogs=false;Render();
        }
    }
    public static char? SelectLine(string line)
    {
        var key=Command(line);
        if(key is >= '1' and <= '9')
        {
            lock(Sync)
            {
                if(key-'1'>=CurrentOptions.Length)return null;
                selection=key.Value-'1';return Navigate(ConsoleKeyAction.Enter);
            }
        }
        return key;
    }
    public static void Show(string heading,string text)
    { lock(Sync) { view="detail"; title=heading; body=Clean(text); serialLogs=false; page=0; Render(); } }
    public static void Page(int change)
    { lock(Sync) { if(view=="qr" && !serialLogs)return; page=Math.Max(0,page+change); Render(); } }
    public static void UpdateLogs(string text)
    { lock(Sync) { logs=Clean(Redaction.Logs(text)); Render(); } }
    public static void OpenLogs()
    { lock(Sync) { view="logs";serialLogs=true; page=0; Render(); } }
    public static void OpenQr(bool reset)
    { lock(Sync) { if(reset)qr=""; view="qr"; title=ConnectedQr?"Scan to open Xur":appliance?.TailscaleState=="Running"?"Tailscale HTTPS":"Tailscale QR enrollment"; serialLogs=false; page=0; Render(); } }
    public static void AppendQr(string text)
    { lock(Sync) { qr+=text; if(qr.Length>32768)qr=qr[^32768..]; Render(); } }
    public static void EndQr(string message)
    { lock(Sync) { if(view!="qr")return; if(appliance?.TailscaleState=="Running"){title=ConnectedQr?"Scan to open Xur":"Tailscale HTTPS";qr="";Render();return;} view="detail"; title="Tailscale"; body=message; qr=""; Render(); } }

    public static char? Command(string line)
    {
        // DA/DSR replies contain digits too. Only an entire option line is a command.
        if(line.Any(char.IsControl))return null;
        line=line.Trim().ToLowerInvariant();
        return line.Length==1 && "0123456789npcdabefghourtvswy".Contains(line[0]) ? line[0] : null;
    }
    public static string Clean(string text)
    {
        text=Regex.Replace(text,@"\x1b\][^\x07]*(?:\x07|\x1b\\)","");
        text=Regex.Replace(text,@"\x1b\[[0-?]*[ -/]*[@-~]","");
        return new string(text.Where(c=>!char.IsControl(c) || c=='\n' || c=='\t').ToArray()).Replace("\t","    ");
    }
    // Kept pure so overflow, pagination and fixed controls can be checked directly.
    public static string Frame(string heading,string text,int columns,int rows,int requestedPage=0,bool qrView=false,bool logWindow=false,int selectedOption=0,string[]? optionList=null,string[]? statusQr=null)
    {
        var options=optionList ?? Options; selectedOption=Math.Clamp(selectedOption,0,options.Length-1);
        columns=Math.Clamp(columns,40,240); rows=Math.Clamp(rows,12,120);
        // Keep the whole interface inside a TV-safe area, including its controls.
        var horizontalMargin=Math.Max(3,(int)Math.Ceiling(columns*0.05));
        var verticalMargin=Math.Max(1,(int)Math.Ceiling(rows*0.05));
        var innerColumns=columns-2*horizontalMargin;
        var innerRows=rows-2*verticalMargin;
        var width=innerColumns-4;
        var optionLimit=Math.Max(1,innerRows-12);
        var optionStart=selectedOption/optionLimit*optionLimit;
        var footer=qrView ? new[]{"> Back to menu"} : logWindow
            ? new[]{"> Back to menu","PgUp/PgDn: Scroll logs"}
            : options.Select((option,index)=>(index==selectedOption ? "> " : "  ")+(index+1)+" "+option).Skip(optionStart).Take(optionLimit).ToArray();
        var height=Math.Max(1,innerRows-footer.Length-5);
        var lines=Clean(text).Split('\n').SelectMany(line=>Wrap(line,width)).ToArray();
        if(statusQr is {Length:>0})
        {
            var qrWidth=statusQr.Max(l=>l.Length);var leftWidth=width-qrWidth-2;
            if(statusQr.Length<=height&&leftWidth>=24)
            {
                var left=Clean(text).Split('\n').SelectMany(line=>Wrap(line,leftWidth)).ToArray();
                lines=Enumerable.Range(0,Math.Max(left.Length,statusQr.Length)).Select(n=>(n<left.Length?left[n]:"").PadRight(leftWidth)+"  "+(n<statusQr.Length?statusQr[n]:new string(' ',qrWidth))).ToArray();
            }
            else lines=[..lines,"Select Tailscale QR to scan the login link."];
        }
        if(qrView && (lines.Length>height || Clean(text).Split('\n').Any(line=>line.Length>width)))
            lines=["Enlarge this terminal window to view the complete QR.","The web setup remains available. 0 returns to the menu."];
        var pages=Math.Max(1,(lines.Length+height-1)/height);
        var selected=Math.Min(requestedPage,pages-1);
        if(logWindow && requestedPage==0) lines=lines.TakeLast(height).ToArray();
        else lines=lines.Skip(selected*height).Take(height).ToArray();
        using var writer=new StringWriter();
        var console=AnsiConsole.Create(new AnsiConsoleSettings { Out=new AnsiConsoleOutput(writer), Ansi=AnsiSupport.No, ColorSystem=ColorSystemSupport.NoColors });
        console.Profile.Width=innerColumns; console.Profile.Height=innerRows; console.Profile.Capabilities.Unicode=true;
        var content=new Panel(new Text(string.Join('\n',lines))).Header("Xur setup | "+Markup.Escape(heading)).RoundedBorder().Expand();
        var controls=new Panel(new Text(string.Join('\n',footer)+"\n"+(qrView ? "Enter: Open | Esc / 0: Menu | Alt+F2: Logs" : "Up/Down: Select | Enter: Open | Esc / 0: Back"))).RoundedBorder().Expand();
        console.Write(new Layout("root").SplitRows(new Layout("content").Update(content),new Layout("controls").Size(footer.Length+3).Update(controls)));
        var frame=writer.ToString().Replace("\r","").TrimEnd('\n').Split('\n');
        var output=new StringBuilder("\x1b%G\x1b[0m\x1b[r\x1b[?25l\x1b[?7l\x1b[H");
        // No newline reaches the terminal. In-place writes cannot scroll at the bottom edge.
        for(int i=0;i<rows;i++)
        {
            var index=i-verticalMargin;
            var line=index>=0 && index<Math.Min(frame.Length,innerRows)?frame[index]:"";
            line=line.PadRight(innerColumns);
            if(line.Contains(qrView || logWindow ? "> Back to menu" : "> "+(selectedOption+1)+" "+options[selectedOption]))
                line=line[..1]+"\x1b[7m"+line[1..^1]+"\x1b[0m"+line[^1..];
            output.Append($"\x1b[{i+1};1H").Append(' ',horizontalMargin).Append(line).Append(' ',horizontalMargin);
        }
        return output.ToString();
    }
    static IEnumerable<string> Wrap(string line,int width)
    {
        if(line.Length==0){yield return "";yield break;}
        for(var i=0;i<line.Length;i+=width)yield return line.Substring(i,Math.Min(width,line.Length-i));
    }
    public static string ExportFrame(int columns,int rows)
    {
        lock(Sync) {
            bool logWindow=serialLogs;
            var content=logWindow?logs:view=="qr"?QrText():body;
            return CachedFrame(logWindow,content,columns,rows);
        }
    }
    static readonly Dictionary<(int,int,bool),(string Key,string Frame)> FrameCache=[];
    static string CachedFrame(bool logWindow,string content,int columns,int rows)
    {
        columns=Math.Clamp(columns,40,240);rows=Math.Clamp(rows,12,120);
        var options=CurrentOptions;var code=!logWindow&&view=="status"?ServeQr():null;
        var key=System.Text.Json.JsonSerializer.Serialize(new{title,content,page,view,selection,options,code});
        var size=(columns,rows,logWindow);
        if(FrameCache.TryGetValue(size,out var cached)&&cached.Key==key)return cached.Frame;
        var frame=Frame(logWindow?"Logs":title,content,columns,rows,page,!logWindow&&view=="qr",logWindow,selection,options,code);
        if(FrameCache.Count>=16)FrameCache.Clear();
        FrameCache[size]=(key,frame);return frame;
    }
    static void Render()
    {
        if(Plain)
        {
            var text=view=="qr"?QrText():body+(view=="status"&&ConnectedQr?"\n"+string.Join('\n',ServeQr()):"")+"\n"+string.Join('\n',CurrentOptions.Select((o,i)=>$"{i+1}. {o}"))+"\n0. Back";
            if(LastFrames.GetValueOrDefault("stdio")==text)return;
            LastFrames["stdio"]=text;Console.WriteLine("Xur setup\n"+text);return;
        }
        QueueRender();
    }
    static int renderPending,renderRunning;
    static void QueueRender()
    {
        Interlocked.Exchange(ref renderPending,1);
        if(Interlocked.CompareExchange(ref renderRunning,1,0)!=0)return;
        _=Task.Run(()=>{
            try
            {
                while(Interlocked.Exchange(ref renderPending,0)!=0)RenderDevices();
            }
            finally{Interlocked.Exchange(ref renderRunning,0);if(Volatile.Read(ref renderPending)!=0)QueueRender();}
        });
    }
    static void RenderDevices()
    {
        var output=new List<(string Path,FileStream Device,string Frame,bool First)>();
        lock(Sync)
        {
            foreach(var path in new[]{"/dev/tty3","/dev/ttyS0","/dev/tty2"})
            {
                if(!Devices.TryGetValue(path,out var file))continue;
                var size=new WindowSize();Ioctl(file.SafeFileHandle.DangerousGetHandle().ToInt32(),0x5413,ref size);
                var columns=size.Columns==0?100:size.Columns;var rows=size.Rows==0?40:size.Rows;
                bool isLog=path=="/dev/tty2" || serialLogs;
                var frame=CachedFrame(isLog,isLog?logs:view=="qr"?QrText():body,columns,rows);
                if(LastFrames.GetValueOrDefault(path)==frame)continue;
                output.Add((path,file,frame,!LastFrames.ContainsKey(path)));
            }
        }
        // Slow serial output must not hold the menu lock or delay DRM/input requests.
        foreach(var item in output)try
        {
            if(item.First)item.Device.Write(Encoding.UTF8.GetBytes("\x1b[?1049h\x1b[2J"));
            item.Device.Write(Encoding.UTF8.GetBytes(item.Frame));item.Device.Flush();
            lock(Sync)LastFrames[item.Path]=item.Frame;
        }catch{ /* A disconnected terminal must not stop the manager. */ }
    }
    public static void Stop()
    {
        if(Plain)return;
        lock(Sync)
        {
            foreach(var file in Devices.Values)
            {
                try { file.Write(Encoding.UTF8.GetBytes("\x1b[?7h\x1b[?25h\x1b[?1049l"));file.Flush(); } catch { }
                file.Dispose();
            }
            Devices.Clear();
        }
    }
    // A daemon must not acquire a controlling terminal while opening a fresh VT.
    // Otherwise closing/changing that terminal can send SIGHUP to the web host.
    public static FileStream OpenDevice(string path,FileAccess access)
    {
        int fd=Open(path,(access==FileAccess.Write?1:0)|0x100|0x80000); // O_NOCTTY | O_CLOEXEC
        if(fd<0)throw new IOException("Console device could not be opened");
        var handle=new SafeFileHandle((IntPtr)fd,ownsHandle:true);
        try { return new FileStream(handle,access); } catch { handle.Dispose();throw; }
    }
    public static void ConfigureInput(FileStream file)
    {
        int fd=file.SafeFileHandle.DangerousGetHandle().ToInt32();
        if(GetAttributes(fd,out var mode)!=0)return;
        // Raw navigation keys, with all forms of line echo disabled. Do not
        // let Ctrl-C from a serial terminal signal the web host.
        const uint disabled=1|2|8|64; // ISIG, ICANON, ECHO, ECHONL
        if((mode.LocalFlags&disabled)==0 && mode.Characters[6]==1 && mode.Characters[5]==0)return;
        mode.LocalFlags&=~disabled;mode.Characters[6]=1;mode.Characters[5]=0;
        SetAttributes(fd,0,ref mode);
    }
    [StructLayout(LayoutKind.Sequential)] struct TerminalAttributes
    {
        public uint InputFlags,OutputFlags,ControlFlags,LocalFlags;
        public byte Line;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] Characters;
        public uint InputSpeed,OutputSpeed;
    }
    [DllImport("libc",EntryPoint="tcgetattr",SetLastError=true)] static extern int GetAttributes(int fd,out TerminalAttributes value);
    [DllImport("libc",EntryPoint="tcsetattr",SetLastError=true)] static extern int SetAttributes(int fd,int when,ref TerminalAttributes value);
    [DllImport("libc",EntryPoint="open",SetLastError=true)] static extern int Open(string path,int flags);
    [DllImport("libc",EntryPoint="ioctl",SetLastError=true)] static extern int ConsoleIoctl(int fd,nuint request,byte[] data);
    [DllImport("libc",EntryPoint="klogctl",SetLastError=true)] static extern int KernelLog(int action,IntPtr buffer,int length);
    [StructLayout(LayoutKind.Sequential)] struct WindowSize { public ushort Rows,Columns,XPixel,YPixel; }
    [DllImport("libc",EntryPoint="ioctl",SetLastError=true)] static extern int Ioctl(int fd,nuint request,ref WindowSize size);
}

public enum ConsoleKeyAction { None,Up,Down,Enter,Back,PageUp,PageDown,One,Two,Three,Four,Five,Six,Seven,Eight,Nine }
// Parse whole escape sequences before accepting a menu key. Device replies
// such as ESC[?6c must never turn their embedded digits into power actions.
public sealed class ConsoleKeyReader
{
    string escape="";
    public bool AwaitingEscape => escape=="\x1b";
    public bool InEscapeSequence => escape.Length>0;
    public ConsoleKeyAction FlushEscape()
    {
        if(!AwaitingEscape)return ConsoleKeyAction.None;
        escape="";return ConsoleKeyAction.Back;
    }
    public ConsoleKeyAction Read(char key)
    {
        if(key=='\x1b'){escape="\x1b";return ConsoleKeyAction.None;}
        if(escape.Length>0)
        {
            escape+=key;
            if(escape.Length==2 && key is '[' or 'O')return ConsoleKeyAction.None;
            if(escape.Length>2 && key is >= '@' and <= '~' || escape.Length==2 || escape.Length>32)
            {
                var value=escape;escape="";
                return value switch { "\x1b[A" or "\x1bOA"=>ConsoleKeyAction.Up,"\x1b[B" or "\x1bOB"=>ConsoleKeyAction.Down,
                    "\x1b[5~"=>ConsoleKeyAction.PageUp,"\x1b[6~"=>ConsoleKeyAction.PageDown,_=>ConsoleKeyAction.None };
            }
            return ConsoleKeyAction.None;
        }
        return key switch { '\r' or '\n'=>ConsoleKeyAction.Enter,'0'=>ConsoleKeyAction.Back,'n' or 'N'=>ConsoleKeyAction.PageDown,
            'p' or 'P'=>ConsoleKeyAction.PageUp,>= '1' and <= '9'=>ConsoleKeyAction.One+(key-'1'),_=>ConsoleKeyAction.None };
    }
}
