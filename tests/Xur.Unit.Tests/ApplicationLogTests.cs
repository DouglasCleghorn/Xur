using Microsoft.Extensions.Logging;
using Xur.Control;
using Xur.Domain;

static class ApplicationLogTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using var output=new StringWriter();using var log=new ApplicationLog("test-control",output);
        var logger=log.CreateLogger("Account");
        logger.LogInformation("ordinary request omitted");
        logger.LogError(new IOException("Account write failed"),"request 123 password=\"private password\" token=ABC-DEF Bearer secret-session hf_abcdefghijk xur_{Key}_{Secret}",new string('a',32),new string('b',64));
        var text=log.Read();
        check(text.Contains("IOException: Account write failed") && text.Contains("request 123") && text.Contains("test-control"),"Application errors retain service, exception and request context");
        check(!text.Contains("private password")&&!text.Contains("ABC-DEF")&&!text.Contains("secret-session")&&!text.Contains("hf_abcdefghijk")&&!text.Contains(new string('b',64)),"Application errors redact credentials before storage");
        check(!output.ToString().Contains("private password")&&!text.Contains("ordinary request omitted"),"Journal output is redacted and routine request logging is disabled");
        using var client=new HttpClient(new OfflineAgent()){BaseAddress=new Uri("http://agent")};
        var diagnostics=new DiagnosticLogs(log,client);var first=await diagnostics.Read();
        check(first.Contains("Account write failed")&&first.Contains("Service journal unavailable"),"Application diagnostics survive an unavailable agent");
        check(first==await diagnostics.Read(),"Unchanged diagnostic logs retain identical content for conditional polling");
        for(var n=0;n<10;n++)log.Write("Bound",LogLevel.Warning,new string('x',50000));
        check(log.Read().Length<=256*1024+10&&!log.Read().Contains("Account write failed"),"Application fallback evicts old entries to bound memory");
        log.Write("Bound",LogLevel.Error,new string('y',500000));
        check(log.Read().Length<=256*1024+10&&log.Read().Contains("[entry truncated]"),"Oversized application errors are bounded");
    }
    sealed class OfflineAgent:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>throw new HttpRequestException("Agent socket unavailable");
    }
}
