using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

public record UnslothConnection(bool Running,string? Url,string Message);

// Own only Unsloth's port. Never reset Serve or replace another service's handler.
public sealed class UnslothServe(Func<string,string[],int,Task<ProcessResult>>? execute=null)
{
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    readonly SemaphoreSlim gate=new(1,1);
    UnslothConnection current=new(false,null,"Load a profile with Unsloth to connect.");
    string lastHost="";DateTimeOffset nextCheck;
    public async Task<UnslothConnection> Refresh(bool running,string tailUrl,DateTimeOffset? time=null)
    {
        await gate.WaitAsync();
        try
        {
            var now=time??DateTimeOffset.UtcNow;
            if(current.Running==running && lastHost==tailUrl && now<nextCheck)return current;
            lastHost=tailUrl;nextCheck=now.AddSeconds(30);
            current=new(running,null,running?"Connect Tailscale and enable HTTPS to open Unsloth.":"Load a profile with Unsloth to connect.");
            if(!Uri.TryCreate(tailUrl,UriKind.Absolute,out var tail) || tail.Scheme!="https" || Uri.CheckHostName(tail.Host)!=UriHostNameType.Dns)return current;
            var port=UnslothWorkload.HttpsPort.ToString();var host=tail.Host+":"+port;
            using var status=await Status();
            var root=status.RootElement;
            bool hasPort=root.TryGetProperty("TCP",out var tcp)&&tcp.TryGetProperty(port,out _) ||
                root.TryGetProperty("Web",out var web)&&web.EnumerateObject().Any(s=>s.Name.EndsWith(":"+port,StringComparison.Ordinal));
            bool owned=Owned(root,port);
            if(!running)
            {
                if(owned)
                {
                    var stopped=await run("tailscale",["serve","--https="+port,"off"],15);
                    if(stopped.ExitCode!=0)return current=current with{Message="Unsloth stopped, but its Tailscale HTTPS route could not be removed. Retrying automatically."};
                }
                return current;
            }
            if(hasPort&&!owned)return current=current with{Message="Tailscale HTTPS port "+port+" is already used by another service. Free that port to connect Unsloth."};
            if(!Configured(root,host))
            {
                var configured=await run("tailscale",["serve","--bg","--https="+port,UnslothWorkload.Backend],15);
                if(configured.ExitCode!=0)return Unavailable(running);
                using var verify=await Status();
                if(!Configured(verify.RootElement,host))return Unavailable(running);
            }
            return current=new(true,new UriBuilder(tail){Port=UnslothWorkload.HttpsPort,Path="/",Query="",Fragment=""}.Uri.AbsoluteUri,"Running · Tailscale HTTPS ready");
        }
        catch{return Unavailable(running);}
        finally{gate.Release();}
    }
    UnslothConnection Unavailable(bool running)=>current=new(running,null,"Unsloth Tailscale HTTPS is unavailable. Check Tailscale and its HTTPS settings. Retrying automatically.");
    async Task<JsonDocument> Status()
    {
        var result=await run("tailscale",["serve","status","--json"],10);
        if(result.ExitCode!=0)throw new IOException("Tailscale Serve status is unavailable.");
        return JsonDocument.Parse(result.Output);
    }
    static bool Configured(JsonElement root,string host)=>Owned(root,UnslothWorkload.HttpsPort.ToString()) &&
        root.TryGetProperty("Web",out var web)&&web.TryGetProperty(host,out _);
    static bool Owned(JsonElement root,string port)
    {
        if(!root.TryGetProperty("TCP",out var tcp)||!tcp.TryGetProperty(port,out var listener)||!listener.TryGetProperty("HTTPS",out var https)||https.ValueKind!=JsonValueKind.True||!root.TryGetProperty("Web",out var web))return false;
        var sites=web.EnumerateObject().Where(s=>s.Name.EndsWith(":"+port,StringComparison.Ordinal)).ToArray();
        return sites.Length>0&&sites.All(s=>s.Value.TryGetProperty("Handlers",out var handlers)&&handlers.EnumerateObject().Count()==1&&
            handlers.TryGetProperty("/",out var handler)&&handler.TryGetProperty("Proxy",out var proxy)&&proxy.GetString()==UnslothWorkload.Backend);
    }
}
