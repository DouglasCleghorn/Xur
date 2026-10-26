using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public static class T3CodeRuntime
{
    public const string Label="io.xur.t3-bootstrap";
    public const string ToolPath=T3CodeWorkload.Home+"/.local/bin:"+T3CodeWorkload.Home+"/.opencode/bin:"+T3CodeWorkload.Home+"/.local/share/xur-tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    public static string Policy {get;}=Canonical.Hash(new{Launcher=T3CodeWorkload.Launcher,Hostname=Environment.MachineName,Path=ToolPath});
    // Saved recipes/images keep their volume and identity. Replace an older
    // container once so its entrypoint also gets the current startup behavior.
    public static string Fingerprint(Workload workload,JsonElement labels)=>
        labels.TryGetProperty(Label,out var bootstrap)&&bootstrap.GetString()==Policy
            ?T3CodeGpuAccess.Fingerprint(workload,labels)
            :Canonical.Hash(new{Workload=workload.Fingerprint,Bootstrap="legacy"});
    public static string[] Command(Recipe recipe)=>["sh","-c",T3CodeWorkload.Launcher,"t3-launcher",..recipe.Command.Skip(1)];
    public static string[] ContainerArguments()=>["--hostname",Environment.MachineName,"--label",Label+"="+Policy,"--env","PATH="+ToolPath];
}
