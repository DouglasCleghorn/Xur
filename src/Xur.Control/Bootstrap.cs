using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using Xur.Domain;

namespace Xur.Control;

public sealed class Bootstrap
{
    readonly TimeProvider clock;
    readonly object sync = new();
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    string code = new string(RandomNumberGenerator.GetBytes(6).Select(b => Alphabet[b & 31]).ToArray());
    DateTimeOffset expires;
    readonly byte[] signingKey;
    readonly ManagerAccountStore accounts;
    public bool AccountConfigured { get { lock(sync)return accounts.Account!=null; } }
    public string? Username { get { lock(sync)return accounts.Account?.Username; } }
    DateTimeOffset window;
    int attempts;
    bool configured;
    public bool Configured { get { lock(sync) return configured; } }
    public void Initialize(string? configuredToken)
    { lock(sync) {
        if(configured || AccountConfigured)return;
        if(configuredToken!=null) {
            code=BootstrapCode.Parse(configuredToken);
            expires=clock.GetUtcNow().AddMinutes(30);
        }
        configured=true;
    } }
    public Bootstrap(TimeProvider? clock = null, byte[]? signingKey = null, string? directory = null) { accounts=new(directory); this.signingKey=signingKey ?? RandomNumberGenerator.GetBytes(32); this.clock = clock ?? TimeProvider.System; window=this.clock.GetUtcNow(); expires=window.AddMinutes(30); }
    public static byte[] LoadSigningKey(string directory)
    {
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var path=Path.Combine(directory,"session-signing.key");
        if(!File.Exists(path))
        {
            using var stream=new FileStream(path,new FileStreamOptions { Mode=FileMode.CreateNew,Access=FileAccess.Write,
                UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite });
            stream.Write(RandomNumberGenerator.GetBytes(32));stream.Flush(true);
        }
        var key=File.ReadAllBytes(path);if(key.Length!=32)throw new IOException("Invalid session signing key");return key;
    }
    public string DisplayCode { get { lock(sync) return AccountConfigured ? "" : clock.GetUtcNow() >= expires ? "Expired; reboot to generate a new code" : code[..3]+"-"+code[3..]; } }
    public (int Status, string? Session) Login(string user, string candidate)
    {
        lock(sync)
        {
            if(AccountConfigured)return (401,null);
            var now = clock.GetUtcNow();
            if(Limited())return (429,null);
            try { candidate=BootstrapCode.Parse(candidate); } catch(FormatException) { return (401,null); }
            if (now >= expires) return (401,null);
            if (user != "xur" || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), SHA256.HashData(Encoding.UTF8.GetBytes(code)))) return (401,null);
            return (200,Issue("xur","bootstrap"));
        }
    }
    bool Limited()
    {
        var now=clock.GetUtcNow();
        if(now-window>=TimeSpan.FromSeconds(30)){ attempts=0;window=now; }
        return ++attempts>5;
    }
    string Issue(string subject,string purpose)
    {
        // No not-before: installer RTC correction must not invalidate the session.
        var jwt=new JwtSecurityToken("xur","xur-control",[new Claim("sub",subject),new Claim("purpose",purpose),new Claim("jti",Guid.NewGuid().ToString("N"))],
            null,clock.GetUtcNow().AddHours(8).UtcDateTime,new SigningCredentials(new SymmetricSecurityKey(signingKey),SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
    public (int Status,string? Session) PasswordLogin(string username,string password)
    {
        lock(sync)
        {
            if(Limited())return (429,null);
            if(!accounts.Verify(username,password))return (401,null);
            return (200,Issue(accounts.Account!.Id,"manager"));
        }
    }
    public (int Status,string? Session,string? Error) CreateAccount(string? setupSession,string username,string password)
    {
        lock(sync)
        {
            if(AccountConfigured)return (409,null,"An account is already configured.");
            if(!CanSetup(setupSession))return (401,null,"Sign in with the access code first.");
            try { accounts.Create(username,password); }
            catch(ArgumentException e){return (400,null,e.Message);}
            code="";expires=DateTimeOffset.MinValue;
            return (200,Issue(accounts.Account!.Id,"manager"),null);
        }
    }
    public bool CanSetup(string? token)
    {
        lock(sync) { var principal=Validate(token);return !AccountConfigured && principal?.FindFirst("sub")?.Value=="xur" && principal.FindFirst("purpose")?.Value is null or "bootstrap"; }
    }
    public bool Authorized(string? cookie)
    {
        lock(sync) { var principal=Validate(cookie);return accounts.Account is { } account && principal?.FindFirst("sub")?.Value==account.Id && principal.FindFirst("purpose")?.Value=="manager"; }
    }
    ClaimsPrincipal? Validate(string? cookie)
    {
        if(cookie==null || cookie.Length>8192)return null;
        try
        {
            var principal=new JwtSecurityTokenHandler { MapInboundClaims=false }.ValidateToken(cookie,new TokenValidationParameters {
                ValidIssuer="xur",ValidAudience="xur-control",IssuerSigningKey=new SymmetricSecurityKey(signingKey),
                ValidAlgorithms=[SecurityAlgorithms.HmacSha256],RequireSignedTokens=true,RequireExpirationTime=true,
                ValidateIssuerSigningKey=true,ValidateLifetime=true,ClockSkew=TimeSpan.Zero,
                LifetimeValidator=(start,end,_,_)=>end is { } expiry && expiry>clock.GetUtcNow().UtcDateTime && (start==null || start<=clock.GetUtcNow().UtcDateTime)
            },out _);
            return principal;
        }
        catch { return null; }
    }
}

public sealed class Appliance
{
    public string RunDirectory { get; } = Environment.GetEnvironmentVariable("XUR_RUN") ?? "/run/xur";
    public HttpClient Agent { get; }
    public bool Installer { get; } = File.ReadAllText("/proc/cmdline").Split(' ').Contains("xur.installer=1") || Environment.GetEnvironmentVariable("XUR_MODE") == "Installer";
    public string StateDirectory=>Installer?RunDirectory:Environment.GetEnvironmentVariable("XUR_STATE")??"/var/lib/xur";
    public string TailscaleState { get; private set; } = "Not connected";
    public string TailUrl { get; private set; } = "";
    public string TailIdentity { get; private set; } = "";
    public bool TailServeReady {get;private set;}
    public string TailServeStatus {get;private set;}="Not configured";
    readonly SemaphoreSlim serveGate=new(1,1);
    DateTimeOffset nextServeCheck;string serveHost="";
    Task<ProcessResult> Command(string exe,string[] args,int timeout)=>commandRunner!=null?commandRunner(exe,args,timeout):Processes.Run(exe,args,timeout);
    public string? ConfirmedAdministrator { get; private set; }
    public bool QrRunning { get; private set; }
    public string TailLoginUrl { get; private set; } = "";
    public int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("XUR_PORT"),out var p) ? p : 8080;
    public string BootId { get; } = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
    readonly Func<string,string[],int,Task<ProcessResult>>? commandRunner;
    readonly Func<NetworkAdapter[]>? networkObserver;
    public Appliance(Func<string,string[],int,Task<ProcessResult>>? commandRunner=null,Func<NetworkAdapter[]>? networkObserver=null)
    {
        this.commandRunner=commandRunner;this.networkObserver=networkObserver;
        Agent = LocalClient.Create(Path.Combine(RunDirectory,"agent.sock"));
        var adminFile = Path.Combine(StateDirectory, "administrator.json");
        if(File.Exists(adminFile))
            try { using var doc=JsonDocument.Parse(File.ReadAllText(adminFile)); ConfirmedAdministrator=doc.RootElement.GetProperty("login").GetString(); } catch { }
    }
    public NetworkAdapter[] Network()
    {
        try{return networkObserver?.Invoke()??ObserveNetwork();}
        catch(Exception e) when(e is NetworkInformationException or System.Net.Sockets.SocketException or IOException){return [];}
    }
    public static NetworkAdapter[] ObserveNetwork()
    {
        var adapters=new List<NetworkAdapter>();
        foreach(var n in NetworkInterface.GetAllNetworkInterfaces())try
        {
            if(n.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
            adapters.Add(new(n.Name,n.OperationalStatus.ToString(),n.NetworkInterfaceType.ToString(),
                n.GetIPProperties().UnicastAddresses.Where(a=>!System.Net.IPAddress.IsLoopback(a.Address)).Select(a=>a.Address.ToString()).Distinct().Order().ToArray()));
        }
        catch(Exception e) when(e is NetworkInformationException or System.Net.Sockets.SocketException or IOException){ /* An adapter may disappear between enumeration and address lookup. */ }
        return adapters.OrderBy(n=>n.Name).ToArray();
    }
    public string[] Urls()=>Installer?[]:Network().Where(n=>n.State=="Up").SelectMany(n=>n.Addresses)
        .Select(a=>System.Net.IPAddress.TryParse(a,out var ip)?ip:null).Where(a=>a!=null&&!a.IsIPv6LinkLocal&&!System.Net.IPAddress.IsLoopback(a))
        .Select(a=>$"https://{(a!.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6?"["+a+"]":a.ToString())}:{Port+363}/").Distinct().ToArray();
    public async Task<JsonElement?> AgentStatus()
    { try { return await Agent.GetFromJsonAsync<JsonElement>("/status"); } catch { return null; } }
    public async Task<Inventory?> Inventory()
    { try { return await Agent.GetFromJsonAsync<Inventory>("/disks"); } catch { return null; } }
    public async Task RefreshTailscale()
    {
        if(Installer){TailscaleState="Available after installation";TailUrl="";TailIdentity="";TailServeReady=false;TailServeStatus="Available after installation";return;}
        try
        {
            var result = await Command("tailscale",["status","--json"],10);
            if (result.ExitCode != 0) { TailscaleState="Unavailable"; TailUrl=""; TailIdentity=""; TailServeReady=false;nextServeCheck=default;TailServeStatus="Not connected";return; }
            using var doc = JsonDocument.Parse(result.Output); var root = doc.RootElement;
            TailscaleState = root.GetProperty("BackendState").GetString() ?? "Unknown";
            if (TailscaleState != "Running") { TailUrl=""; TailIdentity=""; TailServeReady=false;nextServeCheck=default;TailServeStatus="Not connected";return; }
            TailLoginUrl = "";
            var self = root.GetProperty("Self");
            var dns = self.GetProperty("DNSName").GetString()?.TrimEnd('.') ?? "";
            TailUrl="";
            if (Uri.CheckHostName(dns) == UriHostNameType.Dns) TailUrl = "https://" + dns + "/";
            if (root.TryGetProperty("User",out var users) && self.TryGetProperty("UserID",out var uid) && users.TryGetProperty(uid.ToString(),out var user))
                TailIdentity = user.GetProperty("LoginName").GetString() ?? "";
            await RefreshServe();
        }
        catch { TailscaleState = "Unavailable"; TailUrl=""; TailIdentity="";TailServeReady=false;TailServeStatus="Not connected";nextServeCheck=default; }
    }
    async Task RefreshServe()
    {
        if(TailUrl.Length==0){TailServeReady=false;TailServeStatus="Waiting for a Tailscale DNS name";return;}
        if(serveHost==TailUrl&&DateTimeOffset.UtcNow<nextServeCheck || !await serveGate.WaitAsync(0))return;
        try
        {
            serveHost=TailUrl;nextServeCheck=DateTimeOffset.UtcNow.AddSeconds(30);
            var socket=Path.Combine(RunDirectory,"serve.sock");
            if(commandRunner==null&&!File.Exists(socket)){TailServeReady=false;TailServeStatus="Waiting for the web manager";return;}
            var host=new Uri(TailUrl).Host+":443";var target="unix:"+socket;
            bool Configured(ProcessResult result)
            {
                try
                {
                    using var json=JsonDocument.Parse(result.Output);var root=json.RootElement;
                    return result.ExitCode==0&&root.TryGetProperty("TCP",out var tcp)&&tcp.TryGetProperty("443",out var port)&&port.TryGetProperty("HTTPS",out var https)&&https.ValueKind==JsonValueKind.True&&
                        root.TryGetProperty("Web",out var web)&&web.TryGetProperty(host,out var site)&&site.TryGetProperty("Handlers",out var handlers)&&handlers.TryGetProperty("/",out var handler)&&handler.TryGetProperty("Proxy",out var proxy)&&proxy.GetString()==target;
                }catch{return false;}
            }
            var status=await Command("tailscale",["serve","status","--json"],10);
            TailServeReady=Configured(status);
            if(!TailServeReady)
            {
                TailServeStatus="Configuring HTTPS proxy";
                var configured=await Command("tailscale",["serve","--bg","--https=443",target],15);
                TailServeReady=configured.ExitCode==0&&Configured(await Command("tailscale",["serve","status","--json"],10));
            }
            TailServeStatus=TailServeReady?"HTTPS proxy configured":"HTTPS proxy unavailable. Check that HTTPS is enabled in the Tailscale admin console; use the LAN address meanwhile. Retrying automatically.";
        }
        catch{TailServeReady=false;TailServeStatus="HTTPS proxy unavailable. Check Tailscale HTTPS settings; use the LAN address meanwhile. Retrying automatically.";}
        finally{serveGate.Release();}
    }
    public async Task ConfirmAdmin()
    {
        await RefreshTailscale();
        if (TailscaleState != "Running" || TailIdentity.Length == 0) throw new InvalidOperationException("No enrolling identity");
        ConfirmedAdministrator = TailIdentity;
        var path = Path.Combine(StateDirectory,"administrator.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { login = TailIdentity, confirmed = DateTimeOffset.UtcNow }));
        File.SetUnixFileMode(path,UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public string EnrollmentError {get;private set;}="";
    public async Task<bool> StartQr() {if(!Installer&&TailscaleState=="Running"&&TailUrl.Length>0){LocalConsole.OpenQr(true);return true;}return await PrepareEnrollment(true);}
    public Task<bool> StartWebLogin() => PrepareEnrollment(false);
    async Task<bool> PrepareEnrollment(bool console)
    {
        if(Installer){EnrollmentError="Tailscale is available after installation. Complete local setup first.";if(console)LocalConsole.Show("Tailscale",EnrollmentError);return false;}
        ComputerNameStatus? name=null;
        try{name=await Agent.GetFromJsonAsync<ComputerNameStatus>("/computer-name");}catch{}
        if(name is not {Configured:true} || string.IsNullOrWhiteSpace(name.Name))
        {
            EnrollmentError="Save the server name before setting up Tailscale.";
            if(console)LocalConsole.Show("Server name required",EnrollmentError+"\nChoose Server name from the console menu, then return to Tailscale.");
            return false;
        }
        EnrollmentError="";StartEnrollment(console,name.Name);return true;
    }
    public static string? LoginUrl(string line)
    {
        var text=line.Trim();
        return Uri.TryCreate(text,UriKind.Absolute,out var uri) && uri.Scheme=="https" &&
            uri.Host=="login.tailscale.com" && uri.IsDefaultPort && uri.UserInfo.Length==0 &&
            uri.AbsolutePath.StartsWith("/a/",StringComparison.Ordinal) ? uri.AbsoluteUri : null;
    }
    void StartEnrollment(bool console,string hostname)
    {
        lock(this) { if(console)LocalConsole.OpenQr(!QrRunning); if (QrRunning) return; QrRunning = true; TailLoginUrl=""; }
        _ = Task.Run(async () => {
            try
            {
                // The claim is kept in memory for the authenticated page and local console only.
                var info = new ProcessStartInfo("tailscale") { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[]{"up","--qr","--qr-format=small","--hostname="+hostname,"--accept-dns=false"}) info.ArgumentList.Add(arg);
                using var process = Process.Start(info) ?? throw new IOException();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                async Task Relay(StreamReader input)
                {
                    while (await input.ReadLineAsync(timeout.Token) is { } line)
                    {
                        if(LoginUrl(line) is { } url)TailLoginUrl=url;
                        LocalConsole.AppendQr(line+"\n");
                    }
                }
                try { await Task.WhenAll(Relay(process.StandardOutput),Relay(process.StandardError),process.WaitForExitAsync(timeout.Token)); }
                catch { if (!process.HasExited) process.Kill(true); throw; }
                await RefreshTailscale();
                if (TailscaleState == "Running")
                {
                    LocalConsole.EndQr($"Tailscale: {TailUrl}\n{TailServeStatus}. LAN login remains active.");
                }
            }
            catch { LocalConsole.EndQr("Tailscale enrollment ended or is unavailable. LAN setup remains available."); }
            finally { QrRunning = false; TailLoginUrl=""; LocalConsole.EndQr("Enrollment finished. Check Tailscale in the browser."); }
        });
    }
}

public record NetworkAdapter(string Name, string State, string Kind, string[] Addresses);
