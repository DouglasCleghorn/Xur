namespace Xur.Domain;

public record T3SetupRequest(string Action,string Provider="",string? FlowId=null,string? Code=null,T3ModelSetup? Model=null,T3McpSetup? Mcp=null);
public record T3ModelSetup(string Id,string Name,string ModelId,string BaseUrl,string ApiKey,string Certificate,int ContextTokens);
public record T3SetupState(string State,string Message,string? FlowId=null,string? Url=null,string? DeviceCode=null,bool NeedsCode=false,string? Provider=null,bool NeedsCallback=false,string? ProviderInstanceId=null);
public record T3ModelConnectRequest(string WorkloadId,string Harness,string ModelId,string Origin,int ContextTokens=0);

public record T3McpSetup(string Origin,string ApiKey,string Certificate);
