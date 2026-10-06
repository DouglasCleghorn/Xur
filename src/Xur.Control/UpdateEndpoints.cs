using Xur.Domain;
namespace Xur.Control;
public static class UpdateEndpoints
{
    public static void MapUpdates(this WebApplication app,Appliance device)
    {
        async Task<IResult> Status()
        {
            if(device.Installer)return Results.Conflict();
            var result=await device.Agent.GetAsync("/updates");
            return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
        }
        async Task<IResult> Act(OsUpdateAction request,bool browser=false)
        {
            if(device.Installer)return Results.Conflict();
            var result=await device.Agent.PostAsJsonAsync("/updates",request);
            if(result.IsSuccessStatusCode && browser)return Results.Redirect("/updates");
            return Results.Content(await result.Content.ReadAsStringAsync(),"application/json",statusCode:(int)result.StatusCode);
        }
        async Task<IResult> ApplicationStatus()
        {
            if(device.Installer)return Results.Conflict();
            var r=await device.Agent.GetAsync("/application-updates");
            return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
        }
        async Task<IResult> ApplicationAct(ApplicationUpdateRequest request,bool browser=false)
        {
            if(device.Installer)return Results.Conflict();
            var r=await device.Agent.PostAsJsonAsync("/application-updates",request);
            return browser && r.IsSuccessStatusCode ? Results.Redirect(request.Action=="channel"?"/settings#update-channel":request.Action is "configure" or "development"?"/settings#update-channel":"/updates") : Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
        }
        app.MapGet("/api/application-updates",ApplicationStatus);
        app.MapPost("/api/application-updates",(ApplicationUpdateRequest r)=>ApplicationAct(r));
        app.MapGet("/local/application-updates",ApplicationStatus);
        app.MapPost("/local/application-updates/{action}",(string action)=>ApplicationAct(new(action)));
        app.MapPost("/application-updates/action",(Func<HttpContext,Task<IResult>>)(async c=> {
            var f=await c.Request.ReadFormAsync();return await ApplicationAct(new(f["action"].ToString(),f["server"].ToString(),f["development"]=="true",f["channel"].ToString(),f["publicKey"].ToString()),true);
        }));
        app.MapGet("/api/update-all",async Task<IResult>()=> {
            if(device.Installer)return Results.Conflict();
            var r=await device.Agent.GetAsync("/update-all");return Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
        });
        async Task<IResult> UpdateAll(bool browser)
        {
            if(device.Installer)return Results.Conflict();
            var r=await device.Agent.PostAsync("/update-all",null);
            return browser&&r.IsSuccessStatusCode?Results.Redirect("/updates"):Results.Content(await r.Content.ReadAsStringAsync(),"application/json",statusCode:(int)r.StatusCode);
        }
        app.MapPost("/api/update-all",()=>UpdateAll(false));
        app.MapPost("/updates/all",()=>UpdateAll(true));
        app.MapGet("/api/updates",Status);
        app.MapPost("/api/updates",(OsUpdateAction request)=>Act(request));
        app.MapPost("/updates/action",(Func<HttpContext,Task<IResult>>)(async c=> {
            var form=await c.Request.ReadFormAsync();
            OsUpdateSchedule? schedule=null;
            if(form["action"]=="schedule")
            {
                if(!int.TryParse(form["warningMinutes"],out var warning) || form["days"].Any(d=>!int.TryParse(d,out _)))
                    return Results.BadRequest(new{error="Choose valid schedule days and advance notice."});
                schedule=new(form["time"].ToString(),form["days"].Select(d=>int.Parse(d!)).ToArray(),warning);
            }
            return await Act(new(form["action"].ToString(),schedule,form["windowId"].ToString()),true);
        }));
        app.MapGet("/local/updates",Status);
        app.MapPost("/local/updates/{action}",async Task<IResult>(string action,HttpContext context)=> {
            var request=context.Request.HasJsonContentType()?await context.Request.ReadFromJsonAsync<OsUpdateAction>():null;
            return await Act((request??new(action)) with{Action=action});
        });
    }
}
