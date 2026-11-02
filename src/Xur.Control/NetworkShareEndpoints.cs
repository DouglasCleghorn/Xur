using Xur.Domain;
namespace Xur.Control;
public static class NetworkShareEndpoints
{
    public static void MapNetworkShares(this WebApplication app,Appliance appliance)
    {
        async Task<IResult> Relay(string path,HttpContent? content=null)
        {
            if(appliance.Installer)return Results.Conflict(new{error="Network shares are available after installation."});
            using var response=content==null?await appliance.Agent.GetAsync(path):await appliance.Agent.PostAsync(path,content);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        }
        app.MapGet("/api/storage/network",()=>Relay("/storage/network")).WithDescription("Read hosting settings, saved connections, mount status and runtime installation progress. Passwords are never returned. Read this before any change.");
        app.MapPost("/api/storage/network/runtime",()=>Relay("/storage/network/runtime",JsonContent.Create(new{}))).WithDescription("Install or update the current SMB runtime asynchronously. Poll network status until busy is false; restart hosting to use an update.");
        app.MapPost("/api/storage/network/connections",(SmbConnectionRequest r)=>Relay("/storage/network/connections",JsonContent.Create(r))).WithDescription("Save an SMB3 connection using supplied credentials and local UID/GID. Requires an installed runtime. Defaults to read-only and encryption. Keep passwords out of chat/logs; use the browser when secure secret input is unavailable.");
        app.MapPost("/api/storage/network/connections/action",(SmbAction r)=>Relay("/storage/network/connections/action",JsonContent.Create(r))).WithDescription("Connect, test, disconnect or remove a saved connection by ID. Disconnect/removal require assigned workloads to be unloaded. Removal preserves remote files; update profiles that reference a removed ID.");
        app.MapPost("/api/storage/network/hosted",(SmbHostRequest r)=>Relay("/storage/network/hosted",JsonContent.Create(r))).WithDescription("Add an existing local folder with a dedicated SMB account while hosting is stopped. UID/GID must have filesystem access. allowedClients lists IPv4 addresses or CIDR subnets; omitted or empty denies all systems. Keep passwords out of chat/logs; use the browser when secure secret input is unavailable.");
        app.MapPost("/api/storage/network/hosted/access",(SmbShareAccess r)=>Relay("/storage/network/hosted/access",JsonContent.Create(r))).WithDescription("Set a hosted share allowedClients list of IPv4 addresses or CIDR subnets while hosting is stopped. An empty list denies every system; credentials are still required for allowed clients. Stop hosting to disconnect old sessions, change access, then start hosting.");
        app.MapPost("/api/storage/network/hosted/remove",(SmbAction r)=>Relay("/storage/network/hosted/remove",JsonContent.Create(r))).WithDescription("Remove a hosted share by ID while hosting is stopped. Files are retained.");
        app.MapPost("/api/storage/network/server",(SmbServerSettings r)=>Relay("/storage/network/server",JsonContent.Create(r))).WithDescription("Start or restart hosting on a specific LAN/VPN IPv4 address, or stop it with enabled=false. Read status and preserve address when stopping. Publishing exposes configured folders on TCP 445.");
    }
}
