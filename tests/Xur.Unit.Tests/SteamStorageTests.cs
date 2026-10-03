using System.Text.Json;
using Xur.Agent;

static class SteamStorageTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.GetFullPath(".build/evidence/steam-storage/worker-"+Guid.NewGuid().ToString("N"));
        try
        {
            var worker=new SteamStorage(directory,()=>[]);
            await worker.Scan(CancellationToken.None);
            using var data=JsonDocument.Parse(JsonSerializer.Serialize(worker.Status()));
            var status=data.RootElement;
            check(!status.GetProperty("running").GetBoolean()&&status.GetProperty("error").ValueKind==JsonValueKind.Null&&status.GetProperty("finishedAt").ValueKind==JsonValueKind.String,"Embedded Steam sharing helper completes a pass and reports completion without reading host accounts");
            check(status.GetProperty("report").GetProperty("libraries").GetInt32()==0&&File.Exists(Path.Combine(directory,"pairs.sqlite")),"Steam sharing worker records its private cache and aggregate report");
            check(File.GetUnixFileMode(directory)==(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute),"Steam sharing cache directory is owner-only");
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
