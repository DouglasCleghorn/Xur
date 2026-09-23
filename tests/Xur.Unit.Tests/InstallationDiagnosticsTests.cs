using Xur.Agent;
using Xur.Domain;
static class InstallationDiagnosticsTests
{
    public static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"xur-install-diagnostics-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            check(!Redaction.Logs("psk=wifi-secret\nwpa-key=another-secret\nbootstrapToken=bootstrap-secret").Contains("-secret"),"Installer log redaction removes Wi-Fi keys and answer-file tokens");
            var operation=new Operation("test","Installing","Installing",DateTimeOffset.UtcNow);
            check(InstallationDiagnostics.Observe(operation,root,"")==operation,"Missing service observations never imply installation success");
            File.WriteAllText(root+"/install-complete","");
            check(InstallationDiagnostics.Observe(operation,root,"ActiveState=active").Stage=="Complete","Successful installer marker completes the operation");
            File.WriteAllText(root+"/install-failed","");
            check(InstallationDiagnostics.Observe(operation,root,"").Stage=="Failed","Failure marker takes precedence over a contradictory completion marker");
            foreach(var (phase,message) in new[]{("source","before disk installation"),("anaconda","Anaconda failed"),("post","Configuration of the installed system failed")})
            {
                File.WriteAllText(root+"/install-phase",phase);
                check(InstallationDiagnostics.Observe(operation,root,"").Message.Contains(message),"Installer failure identifies phase: "+phase);
            }
            File.Delete(root+"/install-failed");
            check(InstallationDiagnostics.Observe(operation,root,"ActiveState=failed\nResult=exit-code").Stage=="Failed","Failed installer service overrides completion even without an error marker");
            var failed=operation with{Stage="Failed"};
            check(InstallationDiagnostics.Observe(failed,root,"")==failed,"Failed operations cannot transition to success on later polls");
            check(InstallationDiagnostics.FileLogs(root).Contains("No Anaconda log files"),"Pre-Anaconda failure directs users to the service journal");
            File.WriteAllText(root+"/anaconda.log","Older details\nNewest error: fixture\npassword=fixture-secret");
            File.WriteAllText(root+"/xur-post.log","Configuration error: fixture");
            var logs=InstallationDiagnostics.FileLogs(root,2);
            check(logs.Contains("Newest error")&&logs.Contains("Configuration error")&&!logs.Contains("Older details")&&!logs.Contains("fixture-secret"),"Installer diagnostics include bounded redacted Anaconda and configuration log tails");
        }
        finally{Directory.Delete(root,true);}
    }
}
