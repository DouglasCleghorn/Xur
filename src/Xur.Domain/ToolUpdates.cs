namespace Xur.Domain;
public record ToolUpdateInfo(string Id,string Name,string Version,string UpdatesWith,string Detail,string? Image=null,bool? Downloaded=null);
