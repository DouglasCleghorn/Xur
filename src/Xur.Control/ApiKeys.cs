using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xur.Control;

public sealed record ApiKeyInfo(string Id,string Name,string Scope,DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt=null,DateTimeOffset? LastUsedAt=null,long Requests=0,string? LastMethod=null,string? LastPath=null);
public sealed record ApiKeyRecord(ApiKeyInfo Info,string Hash);
public sealed record ApiKeyCreate(string Name,string Scope="diagnostics",int Days=30);
public sealed record ApiKeyCreated(ApiKeyInfo Key,string Token);

public sealed class ApiKeys
{
    readonly object sync=new();
    readonly string path;
    readonly TimeProvider clock;
    ApiKeyRecord[] records;
    public ApiKeys(string directory,TimeProvider? clock=null)
    {
        this.clock=clock??TimeProvider.System;
        path=Path.Combine(directory,"api-keys.json");
        records=File.Exists(path)?JsonSerializer.Deserialize<ApiKeyRecord[]>(File.ReadAllText(path))??throw new IOException("Invalid API key file."):[];
    }
    public ApiKeyInfo[] List(){lock(sync)return records.Select(r=>r.Info).OrderByDescending(r=>r.CreatedAt).ToArray();}
    public ApiKeyCreated Create(ApiKeyCreate request)
    {
        var name=request.Name?.Trim()??"";
        if(name.Length is <1 or >80 || name.Any(char.IsControl))throw new ArgumentException("Use a key name between 1 and 80 characters.");
        if(request.Scope is not ("diagnostics" or "testing" or "automation"))throw new ArgumentException("Choose a valid access level.");
        if(request.Days is <1 or >365)throw new ArgumentException("Choose an expiry between 1 and 365 days.");
        lock(sync)
        {
            if(records.Count(r=>r.Info.RevokedAt==null && r.Info.ExpiresAt>clock.GetUtcNow())>=100)throw new ArgumentException("Revoke an existing key before creating another (100 active keys maximum).");
            var info=new ApiKeyInfo(Guid.NewGuid().ToString("N"),name,request.Scope,clock.GetUtcNow(),clock.GetUtcNow().AddDays(request.Days));
            var token="xur_"+info.Id+"_"+Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            Save([..records,new(info,Hash(token))]);
            return new(info,token);
        }
    }
    public bool Revoke(string id)
    {
        lock(sync)
        {
            if(!records.Any(r=>r.Info.Id==id))return false;
            Save(records.Select(r=>r.Info.Id==id?r with {Info=r.Info with{RevokedAt=r.Info.RevokedAt??clock.GetUtcNow()}}:r).ToArray());
            return true;
        }
    }
    public ApiKeyInfo? Authenticate(string token)
    {
        if(!Regex.IsMatch(token,@"\Axur_[a-f0-9]{32}_[a-f0-9]{64}\z"))return null;
        lock(sync)
        {
            var record=records.FirstOrDefault(r=>r.Info.Id==token.Substring(4,32));
            return record!=null && record.Info.RevokedAt==null && record.Info.ExpiresAt>clock.GetUtcNow()
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(record.Hash),Encoding.ASCII.GetBytes(Hash(token)))?record.Info:null;
        }
    }
    public void Used(string id,string method,string requestPath)
    {
        lock(sync)Save(records.Select(r=>r.Info.Id==id?r with{Info=r.Info with{LastUsedAt=clock.GetUtcNow(),Requests=r.Info.Requests+1,LastMethod=method,LastPath=requestPath}}:r).ToArray());
    }
    static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    void Save(ApiKeyRecord[] updated)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
            {JsonSerializer.Serialize(stream,updated);stream.Flush(true);}
            File.Move(temp,path,true);
            records=updated;
        }
        finally{File.Delete(temp);}
    }

    public static bool Allows(string scope,string method,string path)
    {
        // No key may mint credentials or authenticate as the browser administrator.
        path=path.ToLowerInvariant().TrimEnd('/');
        if(path.Contains('%') || path.Contains("..") || path.Contains('\\') || path.Contains("//"))return false;
        if(!path.StartsWith("/api/") && !path.StartsWith("/inference/"))return false;
        if(path=="/api/api-keys" || path.StartsWith("/api/api-keys/") || path=="/api/bootstrap" || path.StartsWith("/api/auth/") || path.StartsWith("/api/install/"))return false;
        if(scope=="automation")return true;
        if(scope is not ("diagnostics" or "testing"))return false;
        if(method is "GET" or "HEAD")
        {
            if(path is "/api/power/status" or "/api/status" or "/api/system" or "/api/gpus" or "/api/gpu-power" or "/api/models" or "/api/network-usage" or "/api/workstations" or "/api/profiles" or "/api/recipes" or "/api/logs" or "/api/diagnostics/display" or "/api/disks" or "/api/storage/usage" or "/api/tool-updates" or "/api/application-updates" or "/api/updates" or "/api/ntp" or "/api/timezone" or "/api/model-lab/targets" or "/api/benchmarks")return true;
            if(Regex.IsMatch(path,@"\A/api/(workloads/[^/]+/logs|workstations/[^/]+/(graphics|display)|benchmarks/[^/]+(/export)?)\z"))return true;
        }
        return scope=="testing" && ((method=="POST" && (path is "/api/model-lab/chat" or "/api/benchmarks" || Regex.IsMatch(path,@"\A/api/benchmarks/[^/]+/cancel\z")))
            || (method is "GET" or "POST") && path.StartsWith("/inference/"));
    }
}

public static class ApiKeyAccess
{
    public static void UseApiKeys(this WebApplication app,ApiKeys keys,Func<bool> accountConfigured)
    {
        app.Use(async(ctx,next)=>
        {
            var header=ctx.Request.Headers.Authorization.ToString();
            if(!header.StartsWith("Bearer xur_",StringComparison.OrdinalIgnoreCase)){await next();return;}
            ctx.Response.Headers.CacheControl="no-store";
            var key=accountConfigured()?keys.Authenticate(header[7..]):null;
            if(key==null){ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new{error="API key is invalid, expired or revoked."});return;}
            if(!ApiKeys.Allows(key.Scope,ctx.Request.Method,ctx.Request.Path.Value??""))
            {ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new{error="This API key does not allow that operation."});return;}
            keys.Used(key.Id,ctx.Request.Method,ctx.Request.Path.Value??"");
            ctx.Items["apiKey"]=key;
            await next();
        });
    }
    public static void MapApiKeys(this WebApplication app,ApiKeys keys)
    {
        app.MapGet("/api/api-keys",()=>Results.Json(keys.List()));
        app.MapPost("/api/api-keys",(ApiKeyCreate request)=>
        {
            try{return Results.Json(keys.Create(request),statusCode:201);}
            catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        });
        app.MapPost("/api/api-keys/{id}/revoke",(string id)=>keys.Revoke(id)?Results.Ok():Results.NotFound());
    }
}
