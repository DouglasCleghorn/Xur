namespace Xur.Domain;
public record NtpRequest(bool Enabled,string[] Servers);
public record NtpStatus(bool Enabled,bool Active,bool Synchronized,string[] Servers,string Details);
