using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public static class StationFiles
{
    static readonly string Script=ReadScript();
    static string ReadScript(){using var reader=new StreamReader(typeof(StationFiles).Assembly.GetManifestResourceStream("Xur.Agent.StationFiles.py")!);return reader.ReadToEnd();}
    public static void Map(WebApplication app,bool installer)
    {
        app.MapGet("/files/roots",()=>installer?Results.Conflict():Results.Json(StationAccounts.Read(includeTemporary:true)));
        app.MapGet("/files/list",async Task<IResult>(string user,string? path,string? q,HttpContext ctx)=>
        {
            if(installer)return Results.Conflict();
            try
            {
                await using var reader=await Open(user,path??"",q??"","list",ctx.RequestAborted);
                using var header=await reader.Header(ctx.RequestAborted);
                return Results.Json(header.RootElement.Clone(),statusCode:header.RootElement.GetProperty("ok").GetBoolean()?200:400);
            }
            catch(Exception e) when(e is InvalidOperationException or IOException or OperationCanceledException){return Results.BadRequest(new{error="Could not open this workstation folder."});}
        });
        app.MapGet("/files/download",async Task<IResult>(string user,string path,HttpContext ctx)=>
        {
            if(installer)return Results.Conflict();
            Reader? reader=null;
            try
            {
                reader=await Open(user,path,"","download",ctx.RequestAborted);
                using var header=await reader.Header(ctx.RequestAborted);
                if(!header.RootElement.GetProperty("ok").GetBoolean()){await reader.DisposeAsync();return Results.BadRequest(new{error="File unavailable. Open the original file; links are not followed."});}
                var opened=reader;var size=header.RootElement.GetProperty("size").GetInt64();
                return Results.Stream(async stream=>
                {
                    await using var owned=opened;
                    var buffer=new byte[65536];long remaining=size;
                    while(remaining>0)
                    {
                        var count=await owned.Output.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),ctx.RequestAborted);
                        if(count==0)throw new IOException("File changed while downloading.");
                        await stream.WriteAsync(buffer.AsMemory(0,count),ctx.RequestAborted);remaining-=count;
                    }
                },"application/octet-stream",header.RootElement.GetProperty("name").GetString());
            }
            catch(Exception e) when(e is InvalidOperationException or IOException or OperationCanceledException)
            {if(reader!=null)await reader.DisposeAsync();return Results.BadRequest(new{error="Could not download this file."});}
        });
    }
    static Task<Reader> Open(string user,string path,string query,string mode,CancellationToken cancellation)
    {
        var account=StationAccounts.Read(includeTemporary:true).SingleOrDefault(a=>a.Username==user)??throw new InvalidOperationException("Unknown workstation user.");
        if(path.Length>4096||query.Length>200||path.Split('/').Any(p=>p is "." or "..")||path.StartsWith('/')||path.Contains('\0'))throw new InvalidOperationException("Invalid path.");
        cancellation.ThrowIfCancellationRequested();
        var info=new ProcessStartInfo("runuser"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-u",account.Username,"--","/usr/bin/python3","-I","-c",Script,mode,account.Home,path,query})info.ArgumentList.Add(arg);
        return Task.FromResult(new Reader(Process.Start(info)!));
    }
    sealed class Reader(Process process):IAsyncDisposable
    {
        readonly Task<string> errors=process.StandardError.ReadToEndAsync();
        public Stream Output=>process.StandardOutput.BaseStream;
        public async Task<JsonDocument> Header(CancellationToken cancellation)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var bytes=new MemoryStream();var next=new byte[1];
            while(bytes.Length<1048576)
            {
                if(await Output.ReadAsync(next,timeout.Token)==0)throw new IOException("File reader exited.");
                if(next[0]==10)return JsonDocument.Parse(bytes.ToArray());
                bytes.WriteByte(next[0]);
            }
            throw new IOException("File response exceeded its limit.");
        }
        public async ValueTask DisposeAsync()
        {
            if(!process.HasExited)try{process.Kill(true);}catch(InvalidOperationException){}
            await process.WaitForExitAsync();await errors;process.Dispose();
        }
    }
}
