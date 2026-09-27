using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;

static class InstallerDiagnosticsTests
{
    sealed class Source:HttpMessageHandler
    {
        public bool Offline;public int Actions;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(Offline)throw new HttpRequestException("Fixture offline");
            if(request.Method==HttpMethod.Post)Actions++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=request.RequestUri!.AbsolutePath=="/logs"
                ?new StringContent("password=private-password\nBoot log line")
                :JsonContent.Create(new{screen="setup-confirm",body="password=private-password\nDisk review",password="hidden",apiKey="hidden",operation=(string?)null})});
        }
    }
    public static async Task Run(Action<bool,string> check)
    {
        var key=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string Yaml(bool control)=>$"schemaVersion: 1\napiKey: {key}\nallowControl: {control.ToString().ToLowerInvariant()}\n";
        var config=DiagnosticsConfiguration.Parse(Yaml(true));
        check(config.AllowControl&&config.ApiKey==key,"Diagnostic configuration explicitly enables control with a 256-bit key");
        check(!DiagnosticsConfiguration.Parse($"schemaVersion: 1\napiKey: {key}").AllowControl,"Diagnostic control defaults off");
        foreach(var yaml in new[]{"",Yaml(true)+"extra: true",Yaml(true).Replace(key,"weak"),Yaml(true).Replace("schemaVersion: 1","schemaVersion: 2"),Yaml(true)+"---\napiKey: nope",Yaml(true).Replace("allowControl: true","allowControl: []"),Yaml(true)+"apiKey: duplicate"})
        {
            bool rejected=false;try{DiagnosticsConfiguration.Parse(yaml);}catch{rejected=true;}check(rejected,"Invalid or ambiguous diagnostic YAML rejected");
        }
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.build/evidence/diagnostic-api-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        try
        {
            using var cert=InstallerDiagnosticsApi.Certificate(root);
            var source=new Source();using var agent=new HttpClient(source){BaseAddress=new Uri("http://localhost")};
            var consoleSource=new Source();using var console=new HttpClient(consoleSource){BaseAddress=new Uri("http://localhost")};
            foreach(var allow in new[]{false,true})
            {
                await using var api=InstallerDiagnosticsApi.Create(DiagnosticsConfiguration.Parse(Yaml(allow)),cert,agent,console,IPAddress.Loopback,0);
                await api.StartAsync();
                using var handler=new HttpClientHandler{ServerCertificateCustomValidationCallback=(_,seen,_,_)=>seen?.GetCertHashString(HashAlgorithmName.SHA256)==cert.GetCertHashString(HashAlgorithmName.SHA256)};
                using var client=new HttpClient(handler){BaseAddress=new Uri(api.Urls.Single())};
                using(var result=await client.GetAsync("/v1/status"))check(result.StatusCode==HttpStatusCode.Unauthorized,"Diagnostic HTTPS requires an API key");
                using(var result=await client.GetAsync("/v1/status?apiKey="+key))check(result.StatusCode==HttpStatusCode.Unauthorized,"Diagnostic API keys are not accepted in URLs");
                client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","wrong");
                using(var result=await client.GetAsync("/v1/console"))check(result.StatusCode==HttpStatusCode.Unauthorized,"Wrong diagnostic key rejected");
                client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
                using(var result=await client.GetAsync("/v1/status"))
                {
                    var body=await result.Content.ReadAsStringAsync();check(result.IsSuccessStatusCode&&body.Contains("uptimeSeconds")&&body.Contains("bootId")&&!body.Contains(key),"Authenticated status exposes boot identity and uptime without API key");
                    check(result.Headers.CacheControl?.NoStore==true,"Diagnostic responses cannot be cached");
                }
                var menu=await client.GetStringAsync("/v1/console");using(var parsed=JsonDocument.Parse(menu))check(!menu.Contains("private-password")&&!menu.Contains("hidden")&&parsed.RootElement.GetProperty("screen").GetString()=="setup-confirm","Structured menu JSON remains valid and redacts credentials");
                var logs=await client.GetStringAsync("/v1/logs");check(logs.Contains("Boot log line")&&!logs.Contains("private-password"),"Diagnostic logs redact secrets");
                using(var result=await client.PostAsJsonAsync("/v1/console/action",new ConsoleDiagnosticAction("revision",121)))check(result.StatusCode==(allow?HttpStatusCode.OK:HttpStatusCode.Forbidden),"Only explicit allowControl permits setup actions");
                using(var result=await client.PostAsJsonAsync("/v1/console/action",new ConsoleDiagnosticAction("revision",Text:new string('x',5000))))check(result.StatusCode==HttpStatusCode.RequestEntityTooLarge,"Oversized diagnostic actions are rejected before forwarding");
                client.DefaultRequestHeaders.Add("Origin","https://example.invalid");
                using(var result=await client.GetAsync("/v1/status"))check(result.StatusCode==HttpStatusCode.Forbidden,"Browser origins cannot invoke diagnostic API");
                client.DefaultRequestHeaders.Remove("Origin");
                using(var result=await client.GetAsync("/v1/bootstrap-config"))check(result.StatusCode==HttpStatusCode.NotFound,"Private agent routes are not exposed by diagnostics");
                consoleSource.Offline=true;
                using(var result=await client.GetAsync("/v1/console"))check(result.StatusCode==HttpStatusCode.ServiceUnavailable,"Unavailable console reports 503");
                using(var result=await client.GetAsync("/v1/logs"))check(result.IsSuccessStatusCode,"Agent logs remain reachable while the console is down");
                consoleSource.Offline=false;
                await api.StopAsync();
            }
            check(consoleSource.Actions==1,"Read-only API never forwards a mutation");
        }
        finally{Directory.Delete(root,true);}
        var revision=0;var called=0;
        ConsoleDiagnosticSnapshot Snapshot()=>new(revision.ToString(),"setup-confirm","Confirm disk erasure","Disk serial FIXTURE",[new(121,"Yes",true),new(48,"No",true),new(256,"Unavailable",false)],0,false,false,null);
        Task<IResult> Act(ConsoleDiagnosticAction action)=>ConsoleDiagnosticSession.Act(action,Snapshot,()=>revision++,id=>{called++;return Task.CompletedTask;},text=>throw new Exception("Unexpected text"));
        check((await Act(new("old",121))) is IStatusCodeHttpResult{StatusCode:409}&&called==0,"Stale diagnostic actions cannot alter the console");
        check((await Act(new("0",121))) is IStatusCodeHttpResult{StatusCode:409}&&called==0,"Remote Yes requires explicit erase consent");
        check((await Act(new("0",256))) is IStatusCodeHttpResult{StatusCode:400}&&called==0,"Disabled menu options cannot be invoked remotely");
        check((await Act(new("0",Text:"yes"))) is IStatusCodeHttpResult{StatusCode:400}&&called==0,"Text cannot approve disk erasure");
        await Act(new("0",121,ConfirmErase:true));await Act(new("0",121,ConfirmErase:true));
        check(called==1&&revision==1,"An approved diagnostic revision is consumed once even if the screen stays unchanged");
        LocalConsole.OpenMaintenance(new("wifi-password","Wi-Fi password","Enter the password",[new('0',"Back")],"private-password",true));
        var snapshot=LocalConsole.DiagnosticSnapshot();
        check(snapshot.Secret&&snapshot.AcceptsText&&snapshot.InputValue==null&&!JsonSerializer.Serialize(snapshot).Contains("private-password"),"Current console snapshots never expose secret input values");
    }
}
