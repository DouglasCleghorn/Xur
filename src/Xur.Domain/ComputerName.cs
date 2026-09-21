namespace Xur.Domain;
public record ComputerNameStatus(string Name,bool Configured,string? Message=null);
public record ComputerNameRequest(string Name);
