
namespace Xur.Robot;

public static class RoboticsEndpoints
{
    public static void MapRobotics(this WebApplication app, RoboticsRuntime robot)
    {
        // Reject unknown fields (including attempted motor targets) in robotics
        // requests, independent of the serializer settings used by other APIs.
        async Task<IResult> Call<T>(HttpContext context,Func<T,Task<IResult>> action)
        {
            try
            {
                var request=await System.Text.Json.JsonSerializer.DeserializeAsync(context.Request.Body,RobotJson.Type<T>(),context.RequestAborted);
                if(request==null)return BadRequest(new RobotError("Provide a task request."));
                return await action(request);
            }
            catch(System.Text.Json.JsonException){return BadRequest(new RobotError("Invalid robotics request. Use task and skill fields only."));}
            catch(InvalidOperationException e){return Conflict(new RobotError(e.Message));}
        }
        app.MapGet("/api/status",()=>Reply(robot.Status()));
        app.MapPost("/api/estop",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.EmergencyStop(),statusCode:202))));
        app.MapPost("/api/reset-estop",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.ResetEmergencyStop(),statusCode:202))));
        app.MapGet("/api/observation",async Task<IResult>(HttpContext c)=>
        {try{return Reply(await robot.Observation(c.RequestAborted));}catch(InvalidOperationException e){return Conflict(new RobotError(e.Message));}});
        app.MapGet("/api/calibration-assessment",()=>Reply(robot.CalibrationAssessment()));
        app.MapGet("/api/metrology",()=>
        {try{return Reply(robot.Metrology());}catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException){return Conflict(new RobotError(error.Message));}});
        app.MapPost("/api/metrology",async Task<IResult>(HttpContext c)=>await Call<RobotMetrologySettings>(c,r=>Task.FromResult<IResult>(Reply(robot.ConfigureMetrology(r)))));
        app.MapPost("/api/auto-calibrate",async Task<IResult>(HttpContext c)=>await Call<RobotCalibrationRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.AutoCalibrate(),statusCode:202))));
        app.MapPost("/api/start-controller",async Task<IResult>(HttpContext c)=>await Call<RobotControllerRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.StartController(r),statusCode:202))));
        app.MapGet("/api/devices",()=>Reply(RoboticsRuntime.Devices()));
        app.MapGet("/api/camera-capabilities",async Task<IResult>(HttpContext c)=>
        {
            if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding"))
                return BadRequest(new RobotError("Camera inventory inspection takes no device, options or request body."));
            try{return Reply(await robot.CameraCapabilities(c.RequestAborted));}catch(InvalidOperationException e){return Conflict(new RobotError(e.Message));}
        });
        app.MapGet("/api/detection",()=>Reply(robot.Detection()));
        app.MapPost("/api/detect-buses",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.DetectBuses(),statusCode:202))));
        app.MapGet("/api/configuration",()=>Reply(robot.Configuration()));
        app.MapGet("/api/jobs",()=>Reply(robot.Jobs()));
        app.MapGet("/api/jobs/{id}",(string id)=>robot.Job(id) is {} job?Reply(job):Results.NotFound());
        app.MapGet("/api/jobs/{id}/captures",(string id)=>
        {try{return Reply(robot.Captures(id));}catch(InvalidOperationException e){return NotFound(new RobotError(e.Message));}});
        app.MapGet("/api/jobs/{id}/captures/{name}",(string id,string name)=>
        {try{return Results.Bytes(robot.Capture(id,name),"image/jpeg");}catch(InvalidOperationException e){return NotFound(new RobotError(e.Message));}});
        app.MapGet("/api/jobs/{id}/markers",(string id)=>
        {try{return Reply(robot.Markers(id));}catch(InvalidOperationException e){return NotFound(new RobotError(e.Message));}});
        app.MapPost("/api/arm",async Task<IResult>(HttpContext c)=>await Call<RobotArmRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Arm(r)))));
        app.MapPost("/api/controller",async Task<IResult>(HttpContext c)=>await Call<RobotControllerRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Controller(r),statusCode:202))));
        app.MapPost("/api/emotes",async Task<IResult>(HttpContext c)=>await Call<RobotEmoteRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Emote(r),statusCode:202))));
        app.MapPost("/api/tasks",async Task<IResult>(HttpContext c)=>await Call<RobotTaskRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Task(r),statusCode:202))));
        app.MapPost("/api/configure",async Task<IResult>(HttpContext c)=>await Call<RoboticsConfiguration>(c,r=>Task.FromResult<IResult>(Reply(robot.Configure(r)))));
        app.MapPost("/api/record",async Task<IResult>(HttpContext c)=>await Call<RobotRecordRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Record(r),statusCode:202))));
        app.MapPost("/api/train",async Task<IResult>(HttpContext c)=>await Call<RobotTrainRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Train(r),statusCode:202))));
        app.MapPost("/api/skills/review",async Task<IResult>(HttpContext c)=>await Call<RobotSkillReview>(c,r=>Task.FromResult<IResult>(Reply(robot.ReviewSkill(r)))));
        app.MapPost("/api/skills/evaluate",async Task<IResult>(HttpContext c)=>await Call<RobotSkillReview>(c,r=>Task.FromResult<IResult>(Reply(robot.EvaluateSkill(r),statusCode:202))));
        app.MapPost("/api/prepare",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Prepare(),statusCode:202))));
        app.MapPost("/api/stop",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,async r=>Reply(await robot.Stop())));
        app.MapPost("/api/probe",async Task<IResult>(HttpContext c)=>await Call<RobotResetStopRequest>(c,r=>Task.FromResult<IResult>(Reply(robot.Probe(),statusCode:202))));
        app.MapGet("/api/cameras/{camera}",async Task<IResult>(string camera,HttpContext context)=>
        {try{return Results.Bytes(await robot.Camera(camera,context.RequestAborted),"image/jpeg");}catch(InvalidOperationException e){return Conflict(new RobotError(e.Message));}});
    }
    static IResult Reply<T>(T value,int statusCode=200)=>TypedResults.Json(value,RobotJson.Type<T>(),statusCode:statusCode);
    static IResult BadRequest(RobotError error)=>Reply(error,400);
    static IResult Conflict(RobotError error)=>Reply(error,409);
    static IResult NotFound(RobotError error)=>Reply(error,404);

}
