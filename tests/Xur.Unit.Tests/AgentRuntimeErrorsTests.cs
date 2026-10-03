using System.Net;
using Xur.Control;
using Xur.Domain;
public static class AgentRuntimeErrorsTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using var client=new HttpClient(new Failure()){BaseAddress=new Uri("http://localhost")};
        var runtime=new AgentWorkloadRuntime(client);
        try { await runtime.Stop(new RuntimeStop("workstation1","instance"));throw new Exception("Failed stop was accepted"); }
        catch(InvalidOperationException e)
        { check(e.Message.Contains("/workloads/stop") && e.Message.Contains("HTTP 500") && e.Message.Contains("Diagnostics"),"Empty agent stop failures retain the operation, HTTP status and recovery direction"); }
    }
    sealed class Failure:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError){RequestMessage=request,Content=new StringContent("")});
    }
}
