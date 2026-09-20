namespace Xur.Domain;
public record TimezoneStatus(string Current,string[] Zones,bool Automatic=false,DateTimeOffset? RefreshedAt=null,string? Error=null);
public record TimezoneRequest(string Zone="",bool Automatic=false);
