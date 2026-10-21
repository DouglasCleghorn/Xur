using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Xur.Control;
using Xur.Domain;

static class T3ModelTests
{
    sealed class Engine:HttpMessageHandler
    {
        public int Posts;public string? ExpectedWorkload,ExpectedEndpoint,Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Get)return new(HttpStatusCode.OK){Content=JsonContent.Create(new{data=new[]{new{id="deepseek-1017",max_model_len=32768}}})};
            Posts++;ExpectedWorkload=request.Headers.GetValues("X-Xur-Expected-Workload").Single();ExpectedEndpoint=request.Headers.GetValues("X-Xur-Expected-Endpoint").Single();Body=await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[]}\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")};
        }
    }
    public static async Task Run(Action<bool,string> check)
    {
        var directory=Path.GetFullPath(".build/evidence/t3-models-"+Guid.NewGuid().ToString("N"));
        var recipe=new Recipe("model","DeepSeek","image",[],8080,"/health","CPU",0,0,"",Engine:"vLLM");
        var workload=new Workload("12","DeepSeek 1017",recipe,[],"chat");
        var instance=new RuntimeInstance(workload.Id,workload.Fingerprint,"container",1,"boot","http://127.0.0.1:19000","running",[]);
        var profile=new Profile("3","Coding",1,[workload]);var state=new ProfileState([profile],profile,new("one",[],[instance]),null);
        BackendRoute[] routes=[new("chat",workload.Id,instance.Endpoint)];
        var engine=new Engine();using var gateway=new HttpClient(engine){BaseAddress=new("http://localhost/")};var keys=new ApiKeys(directory);
        var fail=false;var storagePresent=true;T3ModelSetup? configured=null;
        Task<T3SetupState> Setup(T3SetupRequest r)
        {
            if(r.Action=="connection-status")return Task.FromResult(new T3SetupState(storagePresent?"Configured":"Missing","Storage inspected"));
            if(!fail){configured=r.Model;storagePresent=true;}
            return Task.FromResult(new T3SetupState(fail?"Failed":"Succeeded",fail?"Tool check failed":"Connected"));
        }
        T3Models New()=>new(directory,()=>Task.FromResult(state),()=>Task.FromResult(routes),gateway,keys,Setup,()=>"public certificate only");
        try
        {
            var models=New();var available=await models.Available();
            check(available.Single() is {State:"Ready",ModelIds:["deepseek-1017"]}&&available[0].ContextLimits!["deepseek-1017"]==32768,"T3 discovers exact model IDs and serving context limits");
            foreach(var origin in new[]{"http://xur.test","https://localhost","https://127.0.0.1","https://user:secret@xur.test","https://xur.test/path","https://xur.test/#fragment"})
            {bool denied=false;try{T3Models.ValidateOrigin(origin);}catch(InvalidOperationException){denied=true;}check(denied,"T3 rejects unreachable or unsafe endpoint origins: "+origin);}
            var connection=await models.Connect(new(workload.Id,"opencode","deepseek-1017","https://xur.test:8443",32768));
            check(connection.Configured&&configured?.BaseUrl=="https://xur.test:8443/api/t3-code/inference/"+connection.Id+"/v1"&&configured.Certificate=="public certificate only","T3 connection receives a workload-bound endpoint and public TLS certificate");
            check(!File.ReadAllText(directory+"/t3-connections.json").Contains(configured!.ApiKey)&&keys.Authenticate(configured.ApiKey)?.Scope=="coding","T3 connection records retain key IDs without bearer secrets");
            check((await New().Connect(new(workload.Id,"opencode","deepseek-1017","https://xur.test:8443",32768))).Id==connection.Id&&keys.List().Length==1,"T3 connections survive restart and repeated connect does not mint extra keys");
            var oldToken=configured.ApiKey;storagePresent=false;
            connection=await models.Connect(new(workload.Id,"opencode","deepseek-1017","https://xur.test:8443",32768));
            check(connection.Configured&&keys.Authenticate(oldToken)==null&&keys.Authenticate(configured.ApiKey)!=null&&keys.List().Length==2,"Reconnect repairs missing T3 storage and revokes the replaced credential");
            DefaultHttpContext Context(string model="deepseek-1017",ApiKeyInfo? key=null)
            {
                var c=new DefaultHttpContext();c.Items["apiKey"]=key??keys.Authenticate(configured!.ApiKey);c.Request.Method="POST";c.Request.ContentType="application/json";
                c.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new{model,messages=new[]{new{role="user",content="hello"}}})));c.Response.Body=new MemoryStream();return c;
            }
            var good=Context();await models.Forward(good,connection.Id,"chat/completions");
            check(good.Response.StatusCode==200&&engine.Posts==1&&engine.ExpectedWorkload==workload.Id&&engine.ExpectedEndpoint==instance.Endpoint&&engine.Body!.Contains("deepseek-1017"),"T3 forwards streaming bodies with gateway workload identity guards");
            var wrong=Context("different");await models.Forward(wrong,connection.Id,"chat/completions");check(wrong.Response.StatusCode==400&&engine.Posts==1,"T3 rejects model substitution");
            var other=keys.Create(new("other","coding",0));var foreign=Context(key:other.Key);await models.Forward(foreign,connection.Id,"chat/completions");check(foreign.Response.StatusCode==403&&engine.Posts==1,"A coding key cannot access another model connection");
            var original=state;state=state with{Active=null,Runtime=state.Runtime with{Instances=[]}};routes=[];
            var unloaded=Context();await models.Forward(unloaded,connection.Id,"chat/completions");
            check(unloaded.Response.StatusCode==503&&engine.Posts==1&&(await models.Available()).Single().State=="Profile not loaded"&&models.Connections().Length==1,"Unloading preserves saved model selection and prevents requests");
            state=original;routes=[new("chat","someone-else",instance.Endpoint)];
            var changed=Context();await models.Forward(changed,connection.Id,"chat/completions");check(changed.Response.StatusCode==503&&engine.Posts==1,"Republishing a route for another workload cannot redirect the saved connection");
            routes=[new("chat",workload.Id,instance.Endpoint)];fail=true;
            bool failed=false;try{await models.Connect(new(workload.Id,"codex","deepseek-1017","https://xur.test:8443",32768));}catch(InvalidOperationException){failed=true;}
            check(failed&&models.Connections().Length==1&&keys.List().Count(k=>k.RevokedAt!=null)==2,"Failed compatibility checks revoke the temporary key and preserve existing connections");
            storagePresent=false;failed=false;
            try{await models.Connect(new(workload.Id,"opencode","deepseek-1017","https://xur.test:8443",32768));}catch(InvalidOperationException){failed=true;}
            check(failed&&models.Connections().Single()==connection&&keys.Authenticate(configured.ApiKey)!=null,"A failed repair restores the original connection and credential");
            check(ApiKeys.Allows("coding","POST","/api/t3-code/inference/"+connection.Id+"/v1/chat/completions")&&!ApiKeys.Allows("coding","POST","/api/profiles/apply"),"Coding credentials are restricted to model inference");
            foreach(var scope in new[]{"coding","automation","testing","diagnostics"})foreach(var endpoint in new[]{"setup","connect","pair"})
                check(!ApiKeys.Allows(scope,"POST","/api/t3-code/"+endpoint),"API keys cannot manage coding credentials: "+scope+" "+endpoint);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
