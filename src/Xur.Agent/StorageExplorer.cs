using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;
public sealed class StorageExplorer
{
    readonly object gate=new();
    readonly Dictionary<string,Scan> scans=[];
    Task? worker;
    record Scan(DateTimeOffset CapturedAt,bool Scanning,JsonElement? Data=null,string? Error=null);
    static readonly string script=ReadScript();
    static string ReadScript(){using var r=new StreamReader(typeof(StorageExplorer).Assembly.GetManifestResourceStream("Xur.Agent.StorageExplorer.py")!);return r.ReadToEnd();}
    public void Map(WebApplication app,bool installer)
    {
        app.MapGet("/storage/mounts",async Task<IResult>()=>{if(installer)return Results.Conflict();try{return Results.Json(await StorageMounts.Read());}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
        app.MapGet("/storage/explore",async Task<IResult>(string id,string? path,bool? refresh)=>
        {
            if(installer)return Results.Conflict();
            path??="";
            if(path.Length>4096||path.StartsWith('/')||path.Contains('\0')||path.Split('/').Any(p=>p is "." or ".."))return Results.BadRequest(new{error="Invalid folder path."});
            StorageMount? mount;
            try{mount=(await StorageMounts.Read()).SingleOrDefault(m=>m.Id==id);}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}
            if(mount==null)return Results.NotFound(new{error="Mount no longer available. Return to storage devices."});
            var key=id+"/"+path;
            lock(gate)
            {
                if(scans.TryGetValue(key,out var prior)&&(!refresh.GetValueOrDefault()||prior.Scanning)&&DateTimeOffset.UtcNow-prior.CapturedAt<TimeSpan.FromMinutes(30))return Results.Json(prior);
                if(worker is {IsCompleted:false})return Results.Json(new{scanning=true,waiting=true});
                if(scans.Count>=50)scans.Remove(scans.OrderBy(p=>p.Value.CapturedAt).First().Key);
                scans[key]=new(DateTimeOffset.UtcNow,true);
                worker=Task.Run(async()=>{
                    Scan result;
                    try
                    {
                        var r=await Processes.Run("/usr/bin/python3",["-I","-c",script,mount.Path,path],25);
                        using var parsed=JsonDocument.Parse(r.Output);
                        result=r.ExitCode==0?new(DateTimeOffset.UtcNow,false,parsed.RootElement.Clone()):new(DateTimeOffset.UtcNow,false,Error:"Could not scan this folder. It may have moved or be a symbolic link.");
                    }
                    catch{result=new(DateTimeOffset.UtcNow,false,Error:"Scan could not finish. Refresh to retry.");}
                    lock(gate)scans[key]=result;
                });return Results.Json(scans[key]);
            }
        });
    }
}
