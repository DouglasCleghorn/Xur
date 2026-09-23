using Xur.Domain;
namespace Xur.Control;

public record ConsolePlanRequest(string Path);

public static class ConsoleSetupEndpoints
{
    public static void MapConsoleSetup(this WebApplication app,Appliance device)
    {
        // The /local namespace is accessible only through the root-private socket.
        async Task<IResult> Forward(string path,object? body=null)
        {
            if(!device.Installer)return Results.Conflict(new{error="This server is already installed."});
            if(body!=null&&(await device.Agent.GetFromJsonAsync<ComputerNameStatus>("/computer-name")) is not {Configured:true})
                return Results.Conflict(new{error="Save the server name before installing."});
            using var response=body==null?await device.Agent.GetAsync(path):await device.Agent.PostAsJsonAsync(path,body);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        }
        app.MapGet("/local/setup/logs",async()=>device.Installer?Results.Text(await device.Agent.GetStringAsync("/installation-logs")):Results.Text("Installation logs are available on the installer."));
        app.MapGet("/local/setup/logs/usb",()=>Forward("/installation-logs/usb"));
        app.MapPost("/local/setup/logs/usb",async (UsbLogRequest request)=> {
            if(!device.Installer)return Results.Conflict();
            using var response=await device.Agent.PostAsJsonAsync("/installation-logs/usb",request);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        });
        app.MapGet("/local/setup/status",()=>Forward("/status"));
        app.MapGet("/local/setup/disks",()=>Forward("/disks"));
        app.MapPost("/local/setup/plan",(ConsolePlanRequest request)=>Forward("/plan",request));
        app.MapPost("/local/setup/approve",(Approval request)=>Forward("/approve",request));
    }
}
