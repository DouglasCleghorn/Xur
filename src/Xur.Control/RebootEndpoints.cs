namespace Xur.Control;

public static class RebootEndpoints
{
    public static void MapReboot(this WebApplication app,HttpClient agent,string bootId)
    {
        async Task<IResult> Request(bool browser)
        {
            try {
                using var response=await agent.PostAsync("/power/reboot",null);
                if(browser && response.IsSuccessStatusCode)return Results.Redirect("/reboot?boot="+bootId);
                return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
            }catch(Exception e) when(e is HttpRequestException or TaskCanceledException) {
                return Results.StatusCode(503);
            }
        }
        app.MapPost("/power/reboot",()=>Request(true));
        app.MapPost("/api/power/reboot",()=>Request(false));
    }
}
