using Xur.Domain;

namespace Xur.Agent;

public static class AudioCppRuntime
{
    public static async Task<string[]> Arguments(string models,string workloadId,string[] nodes,Func<string,string[],int,Task<ProcessResult>>? execute=null)
    {
        if(!Xur.Domain.ProfilePolicy.EntityIdentifier(workloadId))throw new InvalidOperationException("Invalid audio workload ID.");
        // Keep complete upstream packages together, including sidecars and model-manager
        // state. This subtree follows the existing Models storage migration lifecycle.
        var path=Path.Combine(models,"audio.cpp",workloadId);
        Directory.CreateDirectory(path);
        // The upstream image runs as ubuntu. Podman's U option gives that container
        // user ownership of this workload's directory without changing other models.
        var args=new List<string>{"--volume",path+":/app/models:rw,Z,U","--env","HOME=/app/models",
            "--env","XDG_CACHE_HOME=/app/models/.cache","--env","XDG_CONFIG_HOME=/app/models/.config"};
        // Upstream runs as a non-root user; retain access to only the assigned
        // render nodes using their host numeric groups (names differ in images).
        if(nodes.Length>0)
        {
            var run=execute??((string exe,string[] arguments,int seconds)=>Processes.Run(exe,arguments,seconds));
            var result=await run("stat",["-c","%g","--",..nodes],15);
            var groups=result.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries);
            if(result.ExitCode!=0 || groups.Length!=nodes.Length || groups.Any(g=>!uint.TryParse(g,out _)))
                throw new InvalidOperationException("Could not determine audio.cpp GPU device permissions.");
            foreach(var group in groups.Distinct())args.AddRange(["--group-add",group]);
        }
        return args.ToArray();
    }
}
