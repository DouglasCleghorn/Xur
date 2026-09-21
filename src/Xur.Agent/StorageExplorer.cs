using Xur.Domain;
using System.Text.Json;
namespace Xur.Agent;
public sealed class StorageExplorer
{
    static readonly string script=ReadScript();
    static string ReadScript(){using var r=new StreamReader(typeof(StorageExplorer).Assembly.GetManifestResourceStream("Xur.Agent.StorageExplorer.py")!);return r.ReadToEnd();}
    public void Map(WebApplication app,bool installer)
    {
        app.MapGet("/storage/mounts",async Task<IResult>()=>{if(installer)return Results.Conflict();try{return Results.Json(await StorageMounts.Read());}catch(InvalidOperationException e){return Results.BadRequest(new{error=e.Message});}});
        // Compatibility for older clients. Never retain file listings between requests.
        // The Files page uses fresh /storage/files/list and scalar /storage/files/size.
        app.MapGet("/storage/explore",async Task<IResult>(string id,string? path)=>
        {
            if(installer)return Results.Conflict();
            path??="";
            if(path.Length>4096||path.StartsWith('/')||path.Contains('\0')||path.Split('/').Any(p=>p is "." or ".."))return Results.BadRequest(new{error="Invalid folder path."});
            try
            {
                var mount=(await StorageMounts.Read()).SingleOrDefault(m=>m.Id==id);
                if(mount==null)return Results.NotFound(new{error="Mount no longer available."});
                var result=await Processes.Run("/usr/bin/python3",["-I","-c",script,mount.Path,path],25);
                if(result.ExitCode!=0)return Results.BadRequest(new{error="Could not scan this folder."});
                using var data=JsonDocument.Parse(result.Output);
                return Results.Json(new{scanning=false,data=data.RootElement.Clone()});
            }
            catch(Exception e) when(e is InvalidOperationException or JsonException){return Results.BadRequest(new{error="Could not scan this folder."});}
        });
    }
}
