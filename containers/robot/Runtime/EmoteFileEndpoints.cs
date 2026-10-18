namespace Xur.Robot;

public static class EmoteFileEndpoints
{
    public static void MapEmoteFiles(this WebApplication app,EmoteFiles files)
    {
        app.MapGet("/api/emote-files",()=>TypedResults.Json(files.List(),RobotJson.Default.EmoteFileItemArray));
        app.MapPost("/api/emote-files",async Task<IResult>(HttpContext context)=>
        {
            if(context.Request.ContentLength>EmoteFiles.MaxArchiveBytes)return Error("Choose an emote file no larger than 16 MiB.",413);
            using var data=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))!=0)
            {
                if(data.Length+count>EmoteFiles.MaxArchiveBytes)return Error("Choose an emote file no larger than 16 MiB.",413);
                await data.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
            }
            try{return TypedResults.Json(files.Import(data.ToArray()),RobotJson.Default.EmoteFileItem,statusCode:201);}
            catch(Exception e) when(e is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or System.Text.DecoderFallbackException)
            {return Error(e.Message,400);}
        });
        app.MapGet("/api/emote-files/{key}/preview",IResult (string key)=>
        {
            try{return TypedResults.PhysicalFile(files.FilePath(key,"preview.mp4"),"video/mp4",enableRangeProcessing:true);}
            catch(Exception e) when(e is IOException or InvalidOperationException or System.Text.Json.JsonException)
            {return Error("The imported preview is unavailable or failed verification.",404);}
        });
        app.MapGet("/api/emote-files/{key}/download",IResult (string key)=>
        {
            try{return TypedResults.PhysicalFile(files.FilePath(key,"original.xuremot"),"application/zip",fileDownloadName:files.Read(key).Manifest.Id+".xuremot",enableRangeProcessing:true);}
            catch(Exception e) when(e is IOException or InvalidOperationException or System.Text.Json.JsonException)
            {return Error("The original emote file is unavailable or failed verification.",404);}
        });
    }
    static IResult Error(string message,int status)=>TypedResults.Json(new RobotError(message),RobotJson.Default.RobotError,statusCode:status);
}
