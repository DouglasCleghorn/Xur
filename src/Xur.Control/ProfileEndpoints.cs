using Xur.Domain;
namespace Xur.Control;
public static class ProfileEndpoints
{
    public static void MapProfiles(this WebApplication app,Appliance appliance,ProfileManager manager,RecipeCatalog catalog)
    {
        async Task<IResult> Safe(Func<Task<IResult>> action)
        {
            if(appliance.Installer)return Results.Conflict(new {error="Profiles are available after installation."});
            try{return await action();}catch(InvalidOperationException e){return Results.Conflict(new {error=e.Message});}
        }
        app.MapGet("/api/profiles",()=>Safe(async()=>Results.Json(await manager.State())));
        app.MapGet("/api/recipes",()=>Safe(()=>Task.FromResult<IResult>(Results.Json(catalog.Recipes))));
        async Task<IResult> Relay(string path,HttpContent? body=null)
        {
            using var response=body==null?await appliance.Agent.GetAsync(path):await appliance.Agent.PostAsync(path,body);
            return Results.Content(await response.Content.ReadAsStringAsync(),"application/json",statusCode:(int)response.StatusCode);
        }
        app.MapGet("/api/gpus",(int? minutes,DateTimeOffset? since,HttpResponse response)=>Safe(async()=>Results.Json(TelemetryDelta.Filter(await GpuStatusView.Observe(appliance,manager,minutes??15),since,response))));
        app.MapGet("/api/profiles/progress",()=>Safe(async()=>Results.Json((await manager.State()).Operation)));
        app.MapGet("/api/container-jobs",()=>Safe(()=>Relay("/container-jobs")));
        app.MapPost("/api/container-jobs",(ContainerPrepareRequest request)=>Safe(()=>Relay("/container-jobs",JsonContent.Create(request))));
        app.MapGet("/api/container-volumes",()=>Safe(()=>Relay("/container-volumes")));
        app.MapGet("/api/storage/usage",()=>Safe(()=>Relay("/storage-usage")));
        app.MapPost("/api/storage/usage/refresh",()=>Safe(()=>Relay("/storage-usage/refresh",JsonContent.Create(new{}))));
        app.MapPost("/storage/refresh",async()=>await Safe(async()=>{using var r=await appliance.Agent.PostAsJsonAsync("/storage-usage/refresh",new{});r.EnsureSuccessStatusCode();return Results.Redirect("/storage");}));
        app.MapGet("/api/tool-updates",()=>Safe(()=>Relay("/tool-updates")));
        app.MapGet("/api/workstations/identities",()=>Safe(async()=>Results.Json(await manager.Stations())));
        app.MapGet("/api/station-devices",()=>Safe(()=>Relay("/station-devices")));
        app.MapGet("/api/station-users",()=>Safe(()=>Relay("/station-users")));
        app.MapPost("/api/station-users",(StationUserCreate request)=>Safe(()=>Relay("/station-users",JsonContent.Create(request))));
        app.MapGet("/api/catalog/search",(string? q,string? engine)=>Safe(()=>Relay("/catalog/search?q="+Uri.EscapeDataString(q??"")+"&engine="+Uri.EscapeDataString(engine??"llama.cpp"))));
        app.MapGet("/api/catalog/options",(string model,string? engine)=>Safe(()=>Relay("/catalog/options?model="+Uri.EscapeDataString(model)+"&engine="+Uri.EscapeDataString(engine??"llama.cpp"))));
        app.MapPost("/api/catalog/resolve",async Task<IResult>(HttpContext c)=>await Safe(async()=> {
            using var data=await System.Text.Json.JsonDocument.ParseAsync(c.Request.Body);
            return await Relay("/catalog/resolve",new StringContent(data.RootElement.GetRawText(),System.Text.Encoding.UTF8,"application/json"));
        }));
        app.MapPost("/api/profiles",(Profile p)=>Safe(async()=>Results.Json(await manager.Save(p))));
        app.MapPost("/api/profiles/create",(ProfileCreateRequest request)=>Safe(async()=>Results.Json(await manager.Create(request.Copy))));
        app.MapPost("/api/profiles/unload/preview",()=>Safe(async()=>Results.Json(await manager.PreviewUnload())));
        app.MapPost("/api/profiles/{id}/delete",(string id,ProfileDeleteRequest request)=>Safe(async()=>{await manager.Delete(id,request.Revision);return Results.Ok();}));
        app.MapPost("/api/profiles/{id}/preview",(string id)=>Safe(async()=>Results.Json(await manager.Preview(id))));
        app.MapPost("/api/profiles/{id}/load",(string id)=>Safe(async()=>{var plan=await manager.Preview(id);return Results.Json(await manager.Apply(new(plan.Id,plan.Digest)));}));
        app.MapPost("/api/profiles/apply",(Approval p)=>Safe(async()=>Results.Json(await manager.Apply(p))));
        app.MapPost("/api/profiles/cancel",(ProfileCancelRequest request)=>Safe(async()=>{if(string.IsNullOrEmpty(request.Id))throw new InvalidOperationException("Specify the current operation ID.");await manager.Cancel(request.Id);return Results.Accepted();}));
        app.MapPost("/api/profiles/resume",()=>Safe(async()=>{await manager.Resume();return Results.Accepted();}));
        app.MapGet("/api/workloads/{id}/logs",(string id)=>Safe(async()=>Results.Text(await appliance.Agent.GetStringAsync("/workloads/"+Uri.EscapeDataString(id)+"/logs"))));
        app.MapPost("/profiles/create",async Task<IResult> (HttpContext c)=>await Safe(async()=> {
            var f=await c.Request.ReadFormAsync();var profile=await manager.Create(f["copy"].ToString());
            return Results.Redirect("/profiles/edit?id="+profile.Id);
        }));
        app.MapPost("/profiles/save",async Task<IResult> (HttpContext c)=>await Safe(async()=> {
            var f=await c.Request.ReadFormAsync();
            var ids=f["workloadId"].ToArray();var recipes=f["recipe"].ToArray();
            if(ids.Length!=recipes.Length)throw new InvalidOperationException("Incomplete workload form.");
            var selections=new List<WorkloadSelection>();
            var users=f["stationUser"].ToArray();var stationIds=f["stationId"].ToArray();var stationNames=f["stationName"].ToArray();
            var stations=await manager.Stations();
            var accounts=await appliance.Agent.GetFromJsonAsync<StationAccount[]>("/station-users") ?? [];
            var existing=(await manager.State()).Profiles.SingleOrDefault(p=>p.Id==f["id"].ToString());
            for(int i=0;i<ids.Length;i++)
            {
                StationUser? user=null;
                if(catalog.Recipes.SingleOrDefault(r=>r.Id==recipes[i])?.Kind=="Workstation")
                {
                    var chosen=users.ElementAtOrDefault(i);
                    if(stations.SingleOrDefault(s=>s.Id==stationIds.ElementAtOrDefault(i)) is {} station)user=station.User;
                    else if(chosen=="temporary")user=new("temporary",0,true);
                    else if(chosen=="legacy" && existing?.Workloads.SingleOrDefault(w=>w.Id==ids[i]) is {Recipe.Kind:"Workstation",User:null}){}
                    else {var account=accounts.SingleOrDefault(a=>a.Username==chosen) ?? throw new InvalidOperationException("Select a workstation user.");user=new(account.Username,account.Uid);}
                }
                selections.Add(new(ids[i],recipes[i] ?? "",f["gpus-"+i].ToArray().Select(s=>s!).ToArray(),user,stationIds.ElementAtOrDefault(i),stationNames.ElementAtOrDefault(i)));
            }
            if(!long.TryParse(f["revision"],out var revision))throw new InvalidOperationException("Reload the profile form.");
            await manager.SaveSelection(f["id"].ToString(),revision,selections.ToArray(),f.ContainsKey("name")?f["name"].ToString():null);return Results.Redirect("/profiles");
        }));
        app.MapPost("/profiles/delete",async Task<IResult>(HttpContext c)=> {
            if(appliance.Installer)return Results.Conflict();
            try {
                var f=await c.Request.ReadFormAsync();
                if(!long.TryParse(f["revision"],out var revision))throw new InvalidOperationException("Reload the profile before deleting.");
                await manager.Delete(f["id"].ToString(),revision);return Results.Redirect("/profiles");
            }catch(InvalidOperationException e){return Results.Redirect("/profiles?error="+Uri.EscapeDataString(e.Message));}
        });
        app.MapPost("/profiles/unload",async Task<IResult>()=> {
            if(appliance.Installer)return Results.Conflict();
            try {var plan=await manager.PreviewUnload();return Results.Redirect("/profiles/review?id="+plan.Id);}
            catch(InvalidOperationException e){return Results.Redirect("/profiles?error="+Uri.EscapeDataString(e.Message));}
        });
        app.MapPost("/profiles/preview",async Task<IResult> (HttpContext c)=>await Safe(async()=> {
            var f=await c.Request.ReadFormAsync();var plan=await manager.Preview(f["id"].ToString());return Results.Redirect("/profiles/review?id="+plan.Id);
        }));
        app.MapPost("/profiles/load",async Task<IResult>(HttpContext c)=> {
            if(appliance.Installer)return Results.Conflict();
            try {var f=await c.Request.ReadFormAsync();var plan=await manager.Preview(f["id"].ToString());await manager.Apply(new(plan.Id,plan.Digest));return Results.Redirect("/profiles");}
            catch(InvalidOperationException e){return Results.Redirect("/profiles?error="+Uri.EscapeDataString(e.Message));}
        });
        app.MapPost("/profiles/apply",async Task<IResult> (HttpContext c)=>await Safe(async()=> {
            var f=await c.Request.ReadFormAsync();await manager.Apply(new(f["id"].ToString(),f["digest"].ToString()));return Results.Redirect("/profiles");
        }));
        app.MapPost("/profiles/cancel",async Task<IResult>(HttpContext context)=>{
            try {
                var form=await context.Request.ReadFormAsync();var id=form["operationId"].ToString();
                if(id.Length==0)throw new InvalidOperationException("Refresh the page before cancelling.");
                await manager.Cancel(id);return Results.Redirect("/profiles");
            }catch(InvalidOperationException e){return Results.Redirect("/profiles?error="+Uri.EscapeDataString(e.Message));}
        });
        app.MapPost("/profiles/resume",()=>Safe(async()=>{await manager.Resume();return Results.Redirect("/profiles");}));
    }
}
public record ProfileCreateRequest(string? Copy=null);

public record ProfileDeleteRequest(long Revision);

public record ProfileCancelRequest(string Id);
