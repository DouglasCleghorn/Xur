using Xur.Domain;
using Xur.Agent;
public static class FileCleanupTests
{
    public static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-cleanup-"+Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var missing=Path.Combine(root,"run","xur","seats","1.json");
            FileCleanup.DeleteIfPresent(missing);
            check(!Directory.Exists(Path.GetDirectoryName(missing)),"Cleanup tolerates a missing runtime parent without recreating stale seat state");
            Directory.CreateDirectory(Path.GetDirectoryName(missing)!);File.WriteAllText(missing,"fixture");
            FileCleanup.DeleteIfPresent(missing);FileCleanup.DeleteIfPresent(missing);
            check(!File.Exists(missing),"Cleanup removes existing files and repeated cleanup succeeds");
            bool refused=false;try{FileCleanup.DeleteIfPresent(root);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){refused=true;}
            check(refused && Directory.Exists(root),"Cleanup does not hide errors or delete a directory passed as a file");
            var policy=new StationNetworkPolicy(Path.Combine(root,"missing-policy-directory"));policy.Remove("1");
            check(!Directory.Exists(Path.Combine(root,"missing-policy-directory")),"Stopping before network policy initialization needs no policy directory");
        }
        finally{Directory.Delete(root,true);}
    }
}
