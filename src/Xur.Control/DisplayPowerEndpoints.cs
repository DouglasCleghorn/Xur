using System.Text.Json;
using Xur.Domain;
namespace Xur.Control;

public static class DisplayPowerEndpoints
{
    public static void MapDisplayPower(this WebApplication app,HttpClient agent)
    {
        async Task<IResult> Forward(string path,object? request=null,bool browser=false)
        {
            try
            {
                using var response=request==null?await agent.GetAsync(path):await agent.PostAsJsonAsync(path,request);
                var body=await response.Content.ReadAsStringAsync();
                if(!browser)return Results.Content(body,"application/json",statusCode:(int)response.StatusCode);
                var message=response.IsSuccessStatusCode?"CEC command acknowledged.":"CEC request failed. Refresh and try again.";
                try{using var json=JsonDocument.Parse(body);if(json.RootElement.TryGetProperty(response.IsSuccessStatusCode?"message":"error",out var value))message=value.GetString()??message;}catch(JsonException){}
                return Results.Redirect("/displays?"+(response.IsSuccessStatusCode?"message":"error")+"="+Uri.EscapeDataString(message));
            }
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException)
            {return browser?Results.Redirect("/displays?error=Display+service+unavailable.+Refresh+and+try+again."):Results.Json(new{error="Display service unavailable. Refresh and try again."},statusCode:503);}
        }
        app.MapGet("/api/displays",()=>Forward("/displays"));
        app.MapPost("/api/displays/power",(DisplayPowerRequest request)=>Forward("/displays/power",request with{Workstation=null,ConsoleOnly=false}));
        app.MapPost("/api/displays/adapter",(DisplayAdapterRequest request)=>Forward("/displays/adapter",request));
        app.MapGet("/local/displays",()=>Forward("/displays?consoleOnly=true"));
        app.MapPost("/local/displays/power",(DisplayPowerRequest request)=>Forward("/displays/power",request with{Workstation=null,ConsoleOnly=true}));
        app.MapPost("/displays/power",async(HttpContext context)=>{
            var form=await context.Request.ReadFormAsync();return await Forward("/displays/power",new DisplayPowerRequest(form["id"].ToString(),form["action"].ToString()),true);
        });
        app.MapPost("/displays/adapter",async(HttpContext context)=>{
            var form=await context.Request.ReadFormAsync();return await Forward("/displays/adapter",new DisplayAdapterRequest(form["id"].ToString(),form["adapter"].ToString()),true);
        });
    }
}
