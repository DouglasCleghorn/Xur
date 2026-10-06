namespace Xur.Domain;

public record ConnectedDisplay(string Id,string Connector,string Gpu,string Name,string? Workstation,
    string? Adapter,bool CanControl,string? UnavailableReason,string Power="Unknown");
public record CecAdapterStatus(string Id,string Name,string Device,bool CanTransmit,int? Card,int? Connector,string? Error=null);
public record DisplayPowerStatus(ConnectedDisplay[] Displays,CecAdapterStatus[] Adapters);
public record DisplayPowerRequest(string Id,string Action,string? Workstation=null,bool ConsoleOnly=false);
public record DisplayWakeRequest(string? Workstation=null,bool ConsoleOnly=false);
public record DisplayAdapterRequest(string Id,string? Adapter);
public record DisplayPowerResult(string Message,int Woken=0,bool ConsumeInput=false);
