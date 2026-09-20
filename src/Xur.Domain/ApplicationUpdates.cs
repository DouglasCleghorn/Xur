using System.Text.Json;
namespace Xur.Domain;
public record ApplicationVersion(string Id,string Version);
public record ApplicationUpdateOperation(string Id,string Stage,string Message,long Updated);
public record ApplicationUpdateStatus(string Server,ApplicationVersion Current,ApplicationVersion? Previous,ApplicationVersion? Available,ApplicationUpdateOperation? Operation,bool Busy,bool Development=false,string Channel="stable");
public record ApplicationUpdateRequest(string Action,string? Server=null,bool Development=false,string Channel="stable");
public static class ApplicationIdentity
{
    public static readonly string Id=Read();
    static string Read()
    {
        var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","bundle.json"));
        try{return JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("id").GetString()!;}catch{return "development";}
    }
}
