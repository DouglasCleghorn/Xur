using Xur.Domain;

namespace Xur.Agent;

public static class RoboticsEndpoints
{
    public static void MapRobotics(this WebApplication app, RoboticsRuntime robot)
    {
        // Reject unknown fields (including attempted motor targets) in robotics
        // requests, independent of the serializer settings used by other APIs.
        var json=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
        async Task<IResult> Call<T>(HttpContext context,Func<T,Task<IResult>> action)
        {
            try
            {
                var request=await System.Text.Json.JsonSerializer.DeserializeAsync<T>(context.Request.Body,json,context.RequestAborted);
                if(request==null)return Results.BadRequest(new{error="Provide a task request."});
                return await action(request);
            }
            catch(System.Text.Json.JsonException){return Results.BadRequest(new{error="Invalid robotics request. Use task and skill fields only."});}
            catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
        }
        app.MapGet("/robotics/status",()=>Results.Json(robot.Status()));
        app.MapPost("/robotics/estop",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.EmergencyStop(),statusCode:202))));
        app.MapPost("/robotics/reset-estop",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.ResetEmergencyStop(),statusCode:202))));
        app.MapGet("/robotics/observation",async Task<IResult>(HttpContext c)=>
        {try{return Results.Json(await robot.Observation(c.RequestAborted));}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
        app.MapGet("/robotics/calibration-assessment",()=>Results.Json(robot.CalibrationAssessment()));
        app.MapPost("/robotics/auto-calibrate",async Task<IResult>(HttpContext c)=>await Call<RobotCalibrationRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.AutoCalibrate(),statusCode:202))));
        app.MapPost("/robotics/start-controller",async Task<IResult>(HttpContext c)=>await Call<RobotControllerRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.StartController(r),statusCode:202))));
        app.MapGet("/robotics/devices",()=>Results.Json(RoboticsRuntime.Devices()));
        app.MapGet("/robotics/detection",()=>Results.Json(robot.Detection()));
        app.MapPost("/robotics/detect-buses",()=>
        {try{return Results.Json(robot.DetectBuses(),statusCode:202);}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
        app.MapGet("/robotics/configuration",()=>Results.Json(robot.Configuration()));
        app.MapGet("/robotics/jobs",()=>Results.Json(robot.Jobs()));
        app.MapGet("/robotics/jobs/{id}",(string id)=>robot.Job(id) is {} job?Results.Json(job):Results.NotFound());
        app.MapGet("/robotics/jobs/{id}/captures",(string id)=>
        {try{return Results.Json(robot.Captures(id));}catch(InvalidOperationException e){return Results.NotFound(new{error=e.Message});}});
        app.MapGet("/robotics/jobs/{id}/captures/{name}",(string id,string name)=>
        {try{return Results.Bytes(robot.Capture(id,name),"image/jpeg");}catch(InvalidOperationException e){return Results.NotFound(new{error=e.Message});}});
        app.MapGet("/robotics/jobs/{id}/markers",(string id)=>
        {try{return Results.Json(robot.Markers(id));}catch(InvalidOperationException e){return Results.NotFound(new{error=e.Message});}});
        app.MapPost("/robotics/arm",async Task<IResult>(HttpContext c)=>await Call<RobotArmRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.Arm(r)))));
        app.MapPost("/robotics/controller",async Task<IResult>(HttpContext c)=>await Call<RobotControllerRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.Controller(r),statusCode:202))));
        app.MapPost("/robotics/emotes",async Task<IResult>(HttpContext c)=>await Call<RobotEmoteRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.Emote(r),statusCode:202))));
        app.MapPost("/robotics/tasks",async Task<IResult>(HttpContext c)=>await Call<RobotTaskRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.Task(r),statusCode:202))));
        app.MapPost("/robotics/configure",async Task<IResult>(HttpContext c)=>await Call<RoboticsConfiguration>(c,r=>Task.FromResult<IResult>(Results.Json(robot.Configure(r)))));
        app.MapPost("/robotics/record",async Task<IResult>(HttpContext c)=>await Call<RobotRecordRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.Record(r),statusCode:202))));
        app.MapPost("/robotics/train",async Task<IResult>(HttpContext c)=>await Call<RobotTrainRequest>(c,r=>Task.FromResult<IResult>(Results.Json(robot.Train(r),statusCode:202))));
        app.MapPost("/robotics/skills/review",async Task<IResult>(HttpContext c)=>await Call<RobotSkillReview>(c,r=>Task.FromResult<IResult>(Results.Json(robot.ReviewSkill(r)))));
        app.MapPost("/robotics/skills/evaluate",async Task<IResult>(HttpContext c)=>await Call<RobotSkillReview>(c,r=>Task.FromResult<IResult>(Results.Json(robot.EvaluateSkill(r),statusCode:202))));
        app.MapPost("/robotics/prepare",()=>
        {try{return Results.Json(robot.Prepare(),statusCode:202);}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
        app.MapPost("/robotics/stop",async Task<IResult>()=>
        {try{return Results.Json(await robot.Stop());}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
        app.MapPost("/robotics/probe",()=>
        {try{return Results.Json(robot.Probe(),statusCode:202);}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
        app.MapGet("/robotics/cameras/{camera}",async Task<IResult>(string camera,HttpContext context)=>
        {try{return Results.Bytes(await robot.Camera(camera,context.RequestAborted),"image/jpeg");}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
    }
}
