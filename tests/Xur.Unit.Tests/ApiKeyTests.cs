using Xur.Control;
static class ApiKeyTests
{
    sealed class Clock:TimeProvider {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    public static void Run(Action<bool,string> check)
    {
        var dir=Path.Combine(Path.GetTempPath(),"xur-api-key-"+Guid.NewGuid().ToString("N"));var clock=new Clock();
        try
        {
            var store=new ApiKeys(dir,clock);var created=store.Create(new("Assistant"));var token=created.Token;
            check(store.Authenticate(token)?.Name=="Assistant","API key authenticates with its full random secret");
            check(store.Authenticate(token[..^1]+(token[^1]=='0'?'1':'0'))==null && store.Authenticate(token[..40])==null,"API key rejects altered and truncated secrets");
            var path=Path.Combine(dir,"api-keys.json");
            check(!File.ReadAllText(path).Contains(token) && File.GetUnixFileMode(path)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"API keys persist only hashes in an owner-only file");
            store=new(dir,clock);check(store.Authenticate(token)!=null,"API keys survive restart");
            store.Used(created.Key.Id,"GET","/api/system");store=new(dir,clock);
            check(store.List()[0].Requests==1 && store.List()[0].LastUsedAt==clock.Now,"API key usage survives restart without retaining request bodies");
            store.Revoke(created.Key.Id);store=new(dir,clock);check(store.Authenticate(token)==null,"Revocation persists and takes effect immediately");
            var expired=store.Create(new("Short-lived",Days:1));clock.Now=clock.Now.AddDays(1);check(store.Authenticate(expired.Token)==null,"API keys expire at the exact expiry boundary");
            foreach(var scope in new[]{"diagnostics","testing","automation"})
                foreach(var denied in new[]{"/api/api-keys","/api/api-keys/id/revoke","/api/auth/login","/api/bootstrap","/local/login","/settings","/api/install/approve","/API/API-KEYS/","/api/%61pi-keys","/api/../local/login"})
                    check(!ApiKeys.Allows(scope,"POST",denied),"API key cannot escalate via "+scope+" "+denied);
            check(ApiKeys.Allows("diagnostics","GET","/api/workstations/10/graphics") && ApiKeys.Allows("diagnostics","GET","/api/workloads/10/logs"),"Diagnostics keys can read workstation probes and logs");
            check(!ApiKeys.Allows("diagnostics","GET","/api/files/download") && !ApiKeys.Allows("diagnostics","POST","/api/benchmarks") && !ApiKeys.Allows("testing","POST","/api/power/reboot"),"Read/test scopes exclude files and privileged mutations");
            check(ApiKeys.Allows("testing","POST","/api/model-lab/chat") && ApiKeys.Allows("testing","POST","/api/benchmarks") && ApiKeys.Allows("testing","POST","/inference/model/v1/chat/completions"),"Testing keys enable inference and benchmark workloads");
            check(ApiKeys.Allows("automation","POST","/api/workstations/10/stream"),"Automation keys enable streaming control");
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
}
