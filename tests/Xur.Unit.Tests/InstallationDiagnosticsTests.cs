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
            foreach(var (phase,message) in new[]{("clock","before disk erasure"),("source","before disk installation"),("anaconda","Anaconda failed"),("post","Configuration of the installed system failed")})
            {
                File.WriteAllText(root+"/install-phase",phase);
                check(InstallationDiagnostics.Observe(operation,root,"").Message.Contains(message),"Installer failure identifies phase: "+phase);
            }
            File.WriteAllText(root+"/install-error","Time synchronization did not complete. Installation stopped before disk erasure.");
            check(InstallationDiagnostics.Observe(operation,root,"",root).Message.Contains("Time synchronization did not complete"),"Preflight clock errors are shown in installation progress");
            File.Delete(root+"/install-error");
            File.WriteAllText(root+"/packaging.log","bootc output: tls: failed to verify certificate: x509: certificate has expired or is not yet valid: current time 2024-02-25T17:35:02Z is before 2026-08-10T00:00:00Z");
            check(InstallationDiagnostics.Observe(operation,root,"",root).Message.Contains("system time 2024-02-25"),"Captured H255 bootc error explains the incorrect clock in the main progress screen");
            File.Delete(root+"/packaging.log");
            File.Delete(root+"/install-failed");
            check(InstallationDiagnostics.Observe(operation,root,"ActiveState=failed\nResult=exit-code").Stage=="Failed","Failed installer service overrides completion even without an error marker");
            var failed=operation with{Stage="Failed"};
            check(InstallationDiagnostics.Observe(failed,root,"")==failed,"Failed operations cannot transition to success on later polls");
            File.Delete(root+"/install-complete");File.WriteAllText(root+"/install-phase","clock");
            check(InstallationDiagnostics.Observe(operation,root,"",root).Message.Contains("before disk erasure"),"Clock synchronization progress remains visible before Anaconda starts");
            check(InstallationDiagnostics.DownloadFailure("unrelated error")==null,"Unrelated failures retain their original phase message");
            check(InstallationDiagnostics.FileLogs(root).Contains("No Anaconda log files"),"Pre-Anaconda failure directs users to the service journal");
            File.WriteAllText(root+"/anaconda.log","Older details\nNewest error: fixture\npassword=fixture-secret");
            File.WriteAllText(root+"/xur-post.log","Configuration error: fixture");
            var logs=InstallationDiagnostics.FileLogs(root,2);
            check(logs.Contains("Newest error")&&logs.Contains("Configuration error")&&!logs.Contains("Older details")&&!logs.Contains("fixture-secret"),"Installer diagnostics include bounded redacted Anaconda and configuration log tails");
        }
        finally{Directory.Delete(root,true);}
    }
}
