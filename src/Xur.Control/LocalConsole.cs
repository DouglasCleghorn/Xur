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
    static readonly string[] Options = ["Status and login", "Tailscale QR", "IP addresses", "Hardware", "Logs", "Reboot", "Shut down"];
    static readonly string[] UpdateOptions = ["Check for updates", "Update OS", "Toggle automatic updates", "Reboot (interrupts workloads)", "Roll back OS", "Back to updates"];
    static readonly string[] AppUpdateOptions = ["Check for updates", "Update Xur", "Roll back Xur", "Back to updates"];
    static readonly string[] UpdateMenuOptions = ["Xur application", "Operating system", "Back to menu"];
    public static bool ViewingApplicationUpdates { get {lock(Sync)return view=="application-updates";} }
    static string[] CurrentOptions => view=="update-menu" ? UpdateMenuOptions : view=="application-updates" ? AppUpdateOptions : view=="updates" ? UpdateOptions : appliance?.Installer==false ? [..Options,"Updates"] : Options;
    public static bool ViewingUpdates { get { lock(Sync)return view=="updates"; } }
    static bool Plain => Environment.GetEnvironmentVariable("XUR_CONSOLE") == "stdio";
    public const string Menu = "\n1. Status and login\n2. Tailscale QR\n3. IP addresses\n4. Hardware\n5. Logs\n6. Reboot\n7. Shut down\n8. Updates\nSelection: ";

    public static async Task Start(Appliance app, Bootstrap auth)
    {
        appliance=app; bootstrap=auth;
        if(!Plain)
        {
            // Linux VTs can reset termios after their last descriptor closes.
            // Hold each device open before setting no-echo, through shutdown.
            foreach(var path in new[]{"/dev/tty3","/dev/ttyS0","/dev/tty2"})
                try { Devices[path]=OpenDevice(path,FileAccess.Write); } catch { }
            foreach(var path in new[]{"/dev/tty3","/dev/ttyS0"})
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
        (bootstrap.AccountConfigured ? "Sign in:\n  User: "+bootstrap.Username+"\n  Use your password in the web manager." : "Initial login:\n  User: xur\n  Access code: "+bootstrap.DisplayCode)+
        "\n\nWeb manager:\n"+WebAddresses(appliance.Urls())+
        "\n\nTailscale: "+appliance.TailscaleState+(appliance.TailUrl.Length>0?"\n  "+appliance.TailUrl:"");
    public static string WebAddresses(string[] urls) => urls.Length==0 ? "  Waiting for network addresses (DHCP / Tailscale)..." : string.Join('\n',urls.Select(u=>"  "+u));
    static string lastQrUrl="";static string[] serveQr=[];
    static string[] ServeQr()
    {
        var url=appliance?.TailscaleState=="Running"&&bootstrap!=null?ConsoleQr.LoginUrl(appliance.TailUrl,bootstrap.AccountConfigured,bootstrap.DisplayCode):"";
        if(url!=lastQrUrl){lastQrUrl=url;serveQr=url.Length==0?[]:ConsoleQr.Rows(url);}return serveQr;
    }
    static bool ConnectedQr=>appliance?.TailscaleState=="Running"&&appliance.TailUrl.Length>0;
    static string QrText()=>ConnectedQr?string.Join('\n',ServeQr()):qr.Length==0?"Starting real Tailscale enrollment...":qr;
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
        foreach(var path in new[]{"/dev/tty3","/dev/ttyS0"})
            if(Devices.TryGetValue(path,out var device))ConfigureInput(device);
        if(view=="status") body=StatusText();
        if(view=="network") body=NetworkText();
        Render(refreshConsole:true);
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
            if(action==ConsoleKeyAction.Enter)return view=="qr" || serialLogs ? '0' : view=="update-menu" ? "ho0"[selection] : view=="application-updates" ? "efgu"[selection] : view=="updates" ? "cda6bu"[selection] : (char)('1'+selection);
            if(action==ConsoleKeyAction.Back)return view is "updates" or "application-updates" ? 'u' : '0';
            if(view=="qr")return null; // Its single selectable action is Back to menu.
            if(action==ConsoleKeyAction.PageDown)return 'n';
            if(action==ConsoleKeyAction.PageUp)return 'p';
            if(action==ConsoleKeyAction.Up)selection=(selection+CurrentOptions.Length-1)%CurrentOptions.Length;
            else if(action==ConsoleKeyAction.Down)selection=(selection+1)%CurrentOptions.Length;
            else if(action>=ConsoleKeyAction.One && action<=ConsoleKeyAction.Nine)selection=Math.Min(action-ConsoleKeyAction.One,CurrentOptions.Length-1);
            Render();return null;
        }
    }
    public static void OpenUpdateMenu()
    {
        lock(Sync) { view="update-menu";title="Updates";body="";selection=0;serialLogs=false;page=0;Render(); }
    }
    public static void OpenApplicationUpdates(ApplicationUpdateStatus? status,bool refreshOnly=false)
    {
        lock(Sync) {
            if(refreshOnly && view!="application-updates")return;
            if(view!="application-updates")selection=0;
            view="application-updates";title="Xur updates";serialLogs=false;page=0;
            body=status==null ? "Could not load application updates." :
                $"Installed: {status.Current.Version}\nAvailable: {status.Available?.Version ?? "Check for updates"}\nServer: {status.Server}\n\n{status.Operation?.Stage}: {status.Operation?.Message}\n\nLocal build testing can be enabled in web Settings.";
            Render();
        }
    }
    public static void OpenUpdates(OsUpdateStatus? status, string? error=null)
    {
        lock(Sync) {
            if(view!="updates")selection=0;
            view="updates";title="OS updates";serialLogs=false;page=0;
            body=error ?? (status==null ? "Loading..." :
                $"Installed: {status.Current?.Version}\nAvailable: {status.Available?.Version ?? "Check for updates"}\n"+
                $"Pending: {(status.RollbackQueued ? status.Previous?.Version : status.Pending?.Version) ?? "None"}\n"+
                $"Automatic updates: {(status.Automatic ? "On" : "Paused")}\n"+
                $"Operation: {(status.Busy ? "Working" : status.Operation?.Stage ?? "Idle")}\n{status.Operation?.Message}");
            Render();
        }
    }
    public static void Show(string heading,string text)
    { lock(Sync) { view="detail"; title=heading; body=Clean(text); serialLogs=false; page=0; Render(); } }
    public static void Page(int change)
    { lock(Sync) { if(view=="qr" && !serialLogs)return; page=Math.Max(0,page+change); Render(); } }
    public static void UpdateLogs(string text)
    { lock(Sync) { logs=Clean(Redaction.Logs(text)); Render(); } }
    public static void OpenLogs()
    { lock(Sync) { serialLogs=true; page=0; Render(); } }
    public static void OpenQr(bool reset)
    { lock(Sync) { if(reset)qr=""; view="qr"; title=ConnectedQr?"Scan to open Xur":"Tailscale QR enrollment"; serialLogs=false; page=0; Render(); } }
    public static void AppendQr(string text)
    { lock(Sync) { qr+=text; if(qr.Length>32768)qr=qr[^32768..]; Render(); } }
    public static void EndQr(string message)
    { lock(Sync) { if(view!="qr")return; view="detail"; title="Tailscale"; body=message; qr=""; Render(); } }

    public static char? Command(string line)
    {
        // DA/DSR replies contain digits too. Only an entire option line is a command.
        if(line.Any(char.IsControl))return null;
        line=line.Trim().ToLowerInvariant();
        return line.Length==1 && "0123456789npcdabefghou".Contains(line[0]) ? line[0] : null;
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
        var footer=qrView ? new[]{"> Back to menu"} : logWindow
            ? new[]{"Alt+F3: Menu | Serial: 0 Menu | PgUp/PgDn: Scroll"}
            : options.Select((option,index)=>(index==selectedOption ? "> " : "  ")+(index+1)+" "+option).ToArray();
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
            if(!logWindow && line.Contains(qrView ? "> Back to menu" : "> "+(selectedOption+1)+" "+options[selectedOption]))
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
            return Frame(logWindow?"Logs":title,content,columns,rows,page,!logWindow&&view=="qr",logWindow,selection,CurrentOptions,!logWindow&&view=="status"?ServeQr():null);
        }
    }
    static void Render(bool refreshConsole=false)
    {
        if(Plain)
        {
            var text=view=="qr"?QrText():body+(view=="status"&&ConnectedQr?"\n"+string.Join('\n',ServeQr()):"");
            if(LastFrames.GetValueOrDefault("stdio")==text)return;
            LastFrames["stdio"]=text;Console.WriteLine("Xur setup\n"+text);return;
        }
        foreach(var path in new[]{"/dev/tty3","/dev/ttyS0","/dev/tty2"})
        {
            try
            {
                if(!Devices.TryGetValue(path,out var file))continue;
                var size=new WindowSize();Ioctl(file.SafeFileHandle.DangerousGetHandle().ToInt32(),0x5413,ref size);
                var columns=size.Columns==0?100:size.Columns;var rows=size.Rows==0?40:size.Rows;
                bool isLog=path=="/dev/tty2" || path=="/dev/ttyS0" && serialLogs;
                string content=isLog?logs:view=="qr"?QrText():body;
                var frame=Frame(isLog?"Logs":title,content,columns,rows,page,!isLog && view=="qr",isLog,selection,CurrentOptions,!isLog&&view=="status"?ServeQr():null);
                // Anaconda console setup can change rendering without changing VT text.
                // Refresh tty3 in place: padded overwrites never clear a QR before drawing it.
                if(LastFrames.GetValueOrDefault(path)==frame && !(refreshConsole && path=="/dev/tty3"))continue;
                if(!LastFrames.ContainsKey(path))file.Write(Encoding.UTF8.GetBytes("\x1b[?1049h\x1b[2J"));
                file.Write(Encoding.UTF8.GetBytes(frame));file.Flush();LastFrames[path]=frame;
            }
            catch { /* An absent serial or virtual console must not stop Kestrel. */ }
        }
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
