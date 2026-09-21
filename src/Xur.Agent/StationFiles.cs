using System.Diagnostics;
using System.Text.Json;
namespace Xur.Agent;

public static class StationFiles
{
    static readonly string Script=ReadScript();
    static readonly FolderSizeCache Sizes=new();
    static string ReadScript(){using var reader=new StreamReader(typeof(StationFiles).Assembly.GetManifestResourceStream("Xur.Agent.StationFiles.py")!);return reader.ReadToEnd();}
    public record Change(string? Destination=null,bool Confirm=false);
    record Root(string User,string Home,bool ReadOnly,string Scope);
    public static void Map(WebApplication app,bool installer)
    {
        app.MapGet("/files/roots",()=>installer?Results.Conflict():Results.Json(StationAccounts.Read(includeTemporary:true)));
        foreach(var storage in new[]{false,true})
        {
            var prefix=storage?"/storage/files":"/files";
            app.MapGet(prefix+"/list",async Task<IResult>(HttpContext ctx,string? user,string? id,string? path,string? q)=>
            {
                if(installer)return Results.Conflict();
                try
                {
                    var root=await Resolve(storage,user,id);
                    using var data=await Json(root,path??"",q??"","list",ctx.RequestAborted);
                    return Results.Json(data.RootElement.Clone(),statusCode:data.RootElement.GetProperty("ok").GetBoolean()?200:400);
                }
                catch(Exception e) when(Expected(e)){return Results.BadRequest(new{error=e.Message});}
            });
            app.MapGet(prefix+"/size",async Task<IResult>(HttpContext ctx,string? user,string? id,string? path,bool? refresh)=>
            {
                if(installer)return Results.Conflict();
                try
                {
                    var root=await Resolve(storage,user,id);path??="";Validate(path);
                    var folder=path;
                    return Results.Json(Sizes.Get(root.Scope,path,refresh==true,async()=>
                    {
                        using var data=await Json(root,folder,"","size",CancellationToken.None);
                        return data.RootElement.GetProperty("sizes").EnumerateObject().ToDictionary(p=>p.Name,p=>new FolderSizeCache.Size(p.Value.GetProperty("bytes").GetInt64(),p.Value.GetProperty("partial").GetBoolean(),DateTimeOffset.UtcNow));
                    }));
                }
                catch(Exception e) when(Expected(e)){return Results.BadRequest(new{error=e.Message});}
            });
            foreach(var action in new[]{"move","delete"})
            {
                app.MapPost(prefix+"/"+action,async Task<IResult>(HttpContext ctx,string? user,string? id,string path,Change change)=>
                {
                    if(installer)return Results.Conflict();
                    try
                    {
                        var root=await Resolve(storage,user,id);
                        if(root.ReadOnly)return Results.BadRequest(new{error="This mount is read only."});
                        if(action=="delete"&&!change.Confirm)return Results.BadRequest(new{error="Delete confirmation is required."});
                        if(action=="move")Validate(change.Destination??throw new InvalidOperationException("Enter a destination."));
                        using var data=await Json(root,path,action=="delete"?"confirm":change.Destination!,action,ctx.RequestAborted);
                        Sizes.Invalidate(); // Also invalidate after a partial recursive delete.
                        return Results.Json(data.RootElement.Clone(),statusCode:data.RootElement.GetProperty("ok").GetBoolean()?200:400);
                    }
                    catch(Exception e) when(Expected(e)){Sizes.Invalidate();return Results.BadRequest(new{error=e.Message});}
                });
            }
            app.MapGet(prefix+"/download",async Task<IResult>(HttpContext ctx,string? user,string? id,string path,string? format)=>
            {
                if(installer)return Results.Conflict();
                Reader? reader=null;
                try
                {
                    if(format is not (null or "original" or "stored" or "compressed"))return Results.BadRequest(new{error="Unknown download format."});
                    reader=Open(await Resolve(storage,user,id),path,"",format is "stored" or "compressed"?format:"download",ctx.RequestAborted);
                    using var header=await reader.Header(ctx.RequestAborted);
                    if(!header.RootElement.GetProperty("ok").GetBoolean()){await reader.DisposeAsync();return Results.BadRequest(header.RootElement.Clone());}
                    var opened=reader;var metadata=header.RootElement;
                    return Results.Stream(async stream=>
                    {
                        await using var owned=opened;
                        await owned.Output.CopyToAsync(stream,65536,ctx.RequestAborted);
                        await owned.EnsureSuccess(ctx.RequestAborted);
                    },metadata.GetProperty("contentType").GetString(),metadata.GetProperty("name").GetString());
                }
                catch(Exception e) when(Expected(e))
                {if(reader!=null)await reader.DisposeAsync();return Results.BadRequest(new{error=e.Message});}
            });
        }
    }
    static bool Expected(Exception e)=>e is InvalidOperationException or IOException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception;
    static async Task<Root> Resolve(bool storage,string? user,string? id)
    {
        if(storage)
        {
            var mount=(await StorageMounts.Read()).SingleOrDefault(m=>m.Id==id)??throw new InvalidOperationException("Mount no longer available.");
            return new("root",mount.Path,mount.ReadOnly,"mount:"+mount.Id);
        }
        var account=StationAccounts.Read(includeTemporary:true).SingleOrDefault(a=>a.Username==user)??throw new InvalidOperationException("Unknown workstation user.");
        return new(account.Username,account.Home,false,"home:"+account.Uid+":"+account.Home);
    }
    static void Validate(string path)
    {
        if(path.Length>4096||path.Split('/').Any(p=>p is "." or "..")||path.StartsWith('/')||path.Contains('\0'))throw new InvalidOperationException("Invalid path.");
    }
    static async Task<JsonDocument> Json(Root root,string path,string query,string mode,CancellationToken cancellation)
    {
        await using var reader=Open(root,path,query,mode,cancellation);
        return await reader.Header(cancellation);
    }
    static Reader Open(Root root,string path,string query,string mode,CancellationToken cancellation)
    {
        Validate(path);if(query.Length>4096)throw new InvalidOperationException("Input is too long.");
        cancellation.ThrowIfCancellationRequested();
        var info=new ProcessStartInfo("runuser"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-u",root.User,"--","/usr/bin/python3","-I","-c",Script,mode,root.Home,path,query})info.ArgumentList.Add(arg);
        return new Reader(Process.Start(info)!,mode=="delete"?TimeSpan.FromMinutes(2):TimeSpan.FromSeconds(20));
    }
    sealed class Reader(Process process,TimeSpan headerTimeout):IAsyncDisposable
    {
        readonly Task<string> errors=process.StandardError.ReadToEndAsync();
        public Stream Output=>process.StandardOutput.BaseStream;
        public async Task<JsonDocument> Header(CancellationToken cancellation)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(headerTimeout);
            using var bytes=new MemoryStream();var next=new byte[1];
            while(bytes.Length<1048576)
            {
                if(await Output.ReadAsync(next,timeout.Token)==0)throw new IOException("File reader exited.");
                if(next[0]==10)return JsonDocument.Parse(bytes.ToArray());
                bytes.WriteByte(next[0]);
            }
            throw new IOException("File response exceeded its limit.");
        }
        public async Task EnsureSuccess(CancellationToken cancellation)
        {await process.WaitForExitAsync(cancellation);if(process.ExitCode!=0)throw new IOException("Download interrupted because a file changed or could not be read.");}
        public async ValueTask DisposeAsync()
        {
            if(!process.HasExited)try{process.Kill(true);}catch(InvalidOperationException){}
            await process.WaitForExitAsync();await errors;process.Dispose();
        }
    }
}
