using Xur.Domain;
namespace Xur.Control;

public static class DiagnosticAccessEndpoints
{
    public static void MapDiagnosticAccess(this WebApplication app,Appliance appliance)
    {
        app.MapGet("/api/diagnostics/ssh",async Task<IResult>()=>{
            try{return Results.Json(await appliance.Agent.GetFromJsonAsync<DiagnosticSshStatus>("/diagnostics/ssh"));}
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException){return Results.Json(new{error="Diagnostic SSH status is unavailable."},statusCode:503);}
        });
        app.MapGet("/api/diagnostics/system",async Task<IResult>()=>{
            try {
                using var response=await appliance.Agent.GetAsync("/diagnostics/system");
                var body=await response.Content.ReadAsByteArrayAsync();
                return response.IsSuccessStatusCode?Results.File(body,"application/json","xur-system-diagnostics.json")
                    :Results.Content(System.Text.Encoding.UTF8.GetString(body),"application/json",statusCode:(int)response.StatusCode);
            } catch(Exception e) when(e is HttpRequestException or TaskCanceledException) {return Results.Json(new{error="System diagnostics are unavailable. Retry when the agent is ready."},statusCode:503);}
        });
        app.MapPost("/settings/diagnostic-ssh",async Task<IResult>(HttpContext context)=>{
            if(appliance.Installer)return Results.Conflict();
            var form=await context.Request.ReadFormAsync();var mode=form["mode"].ToString();
            if(mode is not ("enabled" or "disabled"))return Error("Choose Enabled or Disabled.");
            try {
                using var response=await appliance.Agent.PostAsJsonAsync("/diagnostics/ssh",new DiagnosticSshRequest(mode=="enabled",form["publicKeys"].ToString()));
                if(response.IsSuccessStatusCode)return Results.Redirect("/settings?sshSaved=true#diagnostic-ssh");
                var message="Could not change diagnostic SSH. Check application logs.";
                try{var body=await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();if(body.TryGetProperty("error",out var value))message=value.GetString()??message;}catch(System.Text.Json.JsonException){}
                return Error(message);
            } catch(Exception e) when(e is HttpRequestException or TaskCanceledException) {return Error("Diagnostic SSH service is unavailable. Refresh before retrying.");}
        });
        static IResult Error(string message)=>Results.Redirect("/settings?sshError="+Uri.EscapeDataString(message)+"#diagnostic-ssh");
    }
}
