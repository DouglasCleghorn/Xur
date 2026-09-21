using Xur.Domain;
namespace Xur.Control;
public static class PagePolling
{
    public static string Tailscale(Appliance device)=>Canonical.Hash(new{device.TailscaleState,device.TailUrl,device.TailServeReady,device.TailServeStatus,device.TailLoginUrl,device.QrRunning,device.ConfirmedAdministrator});
}
