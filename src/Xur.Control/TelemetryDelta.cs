using Xur.Domain;
namespace Xur.Control;
public static class TelemetryDelta
{
    static bool Valid(DateTimeOffset? since,DateTimeOffset? captured)=>since.HasValue&&captured.HasValue&&since<=captured&&since>=captured.Value.AddDays(-1);
    public static GpuStatusView Filter(GpuStatusView data,DateTimeOffset? since,HttpResponse response)
    {
        if(!Valid(since,data.CapturedAt))return data;
        response.Headers["X-Xur-History-Delta"]="true";
        return data with {Cards=data.Cards.Select(c=>c with{Telemetry=c.Telemetry with{History=c.Telemetry.History.Where(p=>p.At>since).ToArray()}}).ToArray()};
    }
    public static NetworkUsageSnapshot Filter(NetworkUsageSnapshot data,DateTimeOffset? since,HttpResponse response)
    {
        if(!Valid(since,data.CapturedAt))return data;
        response.Headers["X-Xur-History-Delta"]="true";
        return data with {Adapters=data.Adapters.Select(a=>a with{History=a.History.Where(p=>p.At>since).ToArray()}).ToArray()};
    }
}
