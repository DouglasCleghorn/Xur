namespace Xur.Control;
public static class BackupEndpoints
{
    public static async Task<IResult> Download(HttpContext context,bool installer,int port,Func<Task<RecoveryArchive>> create)
    {
        context.Response.Headers.CacheControl="no-store";
        if(installer)return Results.Redirect("/settings?error="+Uri.EscapeDataString("Recovery backups are available after installation."));
        if(!context.Request.IsHttps && context.Features.Get<EndpointIdentity>()?.Kind!="serve")
        {
            var host=context.Request.Host.Host;var address=host.Contains(':')?"["+host+"]":host;
            return Results.Redirect("https://"+address+":"+(port+363)+"/settings/backup");
        }
        try
        {
            var archive=await create();
            try{return Results.File(archive.OpenDownload(),"application/zip",archive.DownloadName,enableRangeProcessing:false);}
            catch{archive.Dispose();throw;}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Results.Redirect("/settings?error="+Uri.EscapeDataString("Backup could not be completed. No incomplete archive was downloaded. "+e.Message));
        }
    }
}
