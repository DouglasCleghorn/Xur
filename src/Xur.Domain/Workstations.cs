namespace Xur.Domain;
public record StationStreamStatus(string Id,string State,bool Headless,string? Error=null,int Port=47989,string Version="2026.914.233613");
public record StationPairRequest(string PairingId,string Pin,string Name);
