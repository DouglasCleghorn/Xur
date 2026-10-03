using Xur.Domain;
namespace Xur.Agent;

public static class HostFilesystem
{
    public const string Requirement="This Xur version requires a Btrfs root filesystem. Ext4 installations are legacy and unsupported; back up your data and reinstall with current Xur media.";
    public static async Task RequireBtrfs(Func<string,string[],int,Task<ProcessResult>>? runner=null)
    {
        // bootc may expose composefs/overlay at /. /sysroot is the physical root.
        var result=await (runner??((exe,args,timeout)=>Processes.Run(exe,args,timeout)))("findmnt",["--noheadings","--output","FSTYPE","--target","/sysroot"],10);
        if(result.ExitCode!=0||result.Output.Trim()!="btrfs")throw new InvalidOperationException(Requirement);
    }
}
