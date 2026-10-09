using System.Text.Json;
using Xur.Robot.Backups;
namespace Xur.Robot;
public static class BackupEndpoints
{
    public static void MapRecordingBackups(this WebApplication app,RecordingBackups backups)
    {
        static IResult Reply<T>(T value,int code=200)=>TypedResults.Json(value,BackupJson.Type<T>(),statusCode:code);
        static async Task<IResult> Call<T>(HttpContext context,Func<T,IResult> action)
        {
            try{var request=await JsonSerializer.DeserializeAsync(context.Request.Body,BackupJson.Type<T>(),context.RequestAborted);
                return request==null?Reply(new BackupError("Provide a backup request."),400):action(request);}
            catch(JsonException){return Reply(new BackupError("Invalid backup request fields."),400);}
            catch(Exception error) when(error is InvalidOperationException or IOException){return Reply(new BackupError(error.Message),409);}
        }
        app.MapGet("/api/backups/settings",()=>Reply(backups.Settings()));
        app.MapPost("/api/backups/settings",async Task<IResult>(HttpContext context)=>await Call<BackupSettingsRequest>(context,r=>Reply(backups.Configure(r))));
        app.MapGet("/api/backups/recordings",()=>Reply(backups.Recordings()));
        app.MapPost("/api/backups/retry",async Task<IResult>(HttpContext context)=>await Call<BackupRetryRequest>(context,r=>Reply(backups.Retry(r.RecordingId),202)));
    }
}
