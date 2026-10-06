using Xur.Agent;

static class AmdGpuTargetTests
{
    public static void Run(Action<bool,string> check)
    {
        var root=Path.GetFullPath(Path.Combine(".build","amd-target-"+Guid.NewGuid().ToString("N")));
        void Node(int id,string properties){var path=Path.Combine(root,"class/kfd/kfd/topology/nodes",id.ToString());Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"properties"),properties);}
        try
        {
            check(AmdGpuTarget.Read("0000:65:00.0",root)==null,"Missing KFD topology does not invent an AMD compute architecture");
            Node(0,"vendor_id 0\ndomain 0\nlocation_id 25856\ngfx_target_version 110003\n");
            Node(7,"vendor_id 4098\ndomain 1\nlocation_id 25856\ngfx_target_version 110001\n");
            Node(3,"vendor_id 4098\ndomain 0\nlocation_id 25856\ngfx_target_version 110003\n");
            check(AmdGpuTarget.Read("0000:65:00.0",root)=="gfx1103"&&AmdGpuTarget.Read("0001:65:00.0",root)=="gfx1101","AMD targets match the full PCI address while ignoring CPU nodes and topology numbering");
            check(AmdGpuTarget.Read("0000:66:00.0",root)==null&&AmdGpuTarget.Read("../../properties",root)==null,"Unmatched or invalid GPU addresses cannot select another device's target");
            Node(3,"vendor_id 4098\ndomain 0\nlocation_id 25856\ngfx_target_version 90010\n");
            check(AmdGpuTarget.Read("0000:65:00.0",root)=="gfx90a","KFD decimal target versions preserve hexadecimal GFX revision suffixes");
            foreach(var target in new[]{"0","110099","4294967296","not-a-number"})
            {Node(3,"vendor_id 4098\ndomain 0\nlocation_id 25856\ngfx_target_version "+target+"\n");check(AmdGpuTarget.Read("0000:65:00.0",root)==null,"Invalid KFD target cannot become an image selector: "+target);}
            Node(3,"vendor_id 4098\ndomain 0\nlocation_id 25856\ngfx_target_version 110003\n");
            check(AmdGpuTarget.NeedsNativeVllm(["0000:65:00.0"],root)&&!AmdGpuTarget.NeedsNativeVllm(["0001:65:00.0"],root),"Native gfx1103 preparation applies only to the allocated compute target");
            bool mixed=false;try{AmdGpuTarget.NeedsNativeVllm(["0000:65:00.0","0001:65:00.0"],root);}catch(InvalidOperationException){mixed=true;}
            check(mixed,"Mixed architectures cannot be launched with a gfx1103-only engine");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
