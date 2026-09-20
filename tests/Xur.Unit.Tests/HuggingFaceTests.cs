using Xur.Agent;
using Xur.Domain;
using Xur.Control;
using Microsoft.AspNetCore.Http;
public static class HuggingFaceTests
{
    public static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-hf-test-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new HuggingFaceCredentials(root);check(!store.Configured,"HF token absent by default");
            var token="hf_"+new string('a',24);store.Save(token);
            check(new HuggingFaceCredentials(root).Read()==token,"HF token survives settings reload");
            check(File.GetUnixFileMode(store.TokenPath)==(UnixFileMode.UserRead|UnixFileMode.UserWrite),"HF secret is owner-only");
            using var good=store.Request("https://huggingface.co/org/model/resolve/main/model.gguf");
            check(good.Headers.Authorization?.Parameter==token,"HF downloads use saved bearer");
            foreach(var url in new[]{"http://huggingface.co/file","https://huggingface.co.evil.test/file","https://raw.githubusercontent.com/file","https://huggingface.co:444/file"})
            {using var request=store.Request(url);check(request.Headers.Authorization==null,"HF token not sent to "+url);}
            check(!string.Join(' ',store.ContainerArguments()).Contains(token) && store.ContainerArguments().Contains("HF_TOKEN_PATH=/run/secrets/huggingface-token"),"Hub containers read token file, not argv secrets");
            check(!Redaction.Logs("Authorization: Bearer "+token).Contains(token),"Raw HF token redacted from logs");
            var invalid=false;try{store.Save(token+"\nmalicious");}catch(InvalidOperationException){invalid=true;}
            check(invalid && store.Read()==token,"Invalid token does not replace saved credentials");
            store.Save(null);check(!store.Configured,"HF token can be removed");
            var at=DateTimeOffset.UtcNow;var network=new NetworkUsageSnapshot(at,[new("eth0","up",new(at,1,2,3,4),[new(at.AddSeconds(-5),1,2,3,4),new(at,1,2,3,4)])]);
            var response=new DefaultHttpContext().Response;var delta=TelemetryDelta.Filter(network,at.AddSeconds(-5),response);
            check(delta.Adapters[0].History.Length==1 && response.Headers["X-Xur-History-Delta"]=="true","History delta sends only samples newer than cursor");
            check(TelemetryDelta.Filter(network,at.AddSeconds(5),new DefaultHttpContext().Response).Adapters[0].History.Length==2,"Future history cursor resets to full history");
            RegistryMirror.Ensure(Path.Combine(root,"registries"));check(File.ReadAllText(Path.Combine(root,"registries","99-xur-docker-hub.conf"))==RegistryMirror.Configuration,"Registry mirror installed from maintained policy");
            var ctx=new DefaultHttpContext();ctx.Request.Scheme="https";ctx.Request.Host=new HostString("xur.example",8443);ctx.Request.Method="POST";
            ctx.Request.Headers.Origin="https://xur.example:8443";check(BrowserSecurity.SameOrigin(ctx.Request),"Exact browser origin accepted");
            foreach(var origin in new[]{"null","http://xur.example:8443","https://xur.example","https://evil.example","https://xur.example:8443/path"}){ctx.Request.Headers.Origin=origin;check(!BrowserSecurity.SameOrigin(ctx.Request),"Reject origin "+origin);}
            ctx.Request.Headers.Remove("Origin");ctx.Request.Headers["Sec-Fetch-Site"]="same-site";check(!BrowserSecurity.SameOrigin(ctx.Request),"Sibling origin browser request rejected");
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
