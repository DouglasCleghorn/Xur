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
            check(Redaction.Logs("▄▀▄▀\nFetching layers █░ 32/128\n└ Fetching █░ 8.00 MiB/16.00 MiB (2.00 MiB/s)").StartsWith("Fetching layers █░ 32/128"),"Log redaction retains native progress bars while removing QR payloads");
            var operation=new Operation("test","Installing","Installing",DateTimeOffset.UtcNow);
            check(InstallationDiagnostics.Observe(operation,root,"")==operation,"Missing service observations never imply installation success");
            File.WriteAllText(root+"/install-complete","");
            check(InstallationDiagnostics.Observe(operation,root,"ActiveState=active") is {Stage:"Complete",Progress.CompletedSteps:5},"Successful installer marker completes all five installation stages");
            File.WriteAllText(root+"/install-failed","");
            check(InstallationDiagnostics.Observe(operation,root,"").Stage=="Failed","Failure marker takes precedence over a contradictory completion marker");
            foreach(var (phase,message) in new[]{("clock","before disk erasure"),("anaconda","Anaconda failed"),("post","Configuration of the installed system failed")})
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
            check(InstallationDiagnostics.Progress("anaconda","") is {CompletedSteps:1,CurrentStep:"Prepare approved disk"},"Disk preparation follows clock synchronization directly");
            check(InstallationDiagnostics.Progress("anaconda","layers needed: 128 (5.6 GB)") is {CompletedSteps:2,CurrentStep:"Download OS image"},"Download progress reports completed stages without inventing a byte percentage");
            check(InstallationDiagnostics.Progress("anaconda","layers needed: 128 (5.6\u00a0GB)").Detail=="Downloading 128 image layers (5.6 GB).","Download detail shows the real installer layer count and size, including nonbreaking spaces");
            check(InstallationDiagnostics.Progress("anaconda","layers needed: 128\nlayers needed: 12 (1.2 GB)").Detail=="Downloading 12 image layers (1.2 GB).","Download detail uses the latest layer summary");
            check(InstallationDiagnostics.Progress("anaconda","layers needed: 128").Detail=="Downloading 128 image layers.","Download detail remains useful without a reported size");
            check(InstallationDiagnostics.Progress("anaconda","layers needed: 128\nDeploying container image...done") is {CompletedSteps:3},"Deployment advances the stage bar after the download");
            check(InstallationDiagnostics.Progress("post","") is {CompletedSteps:4},"Configuration leaves the stage bar incomplete until success is confirmed");
            File.WriteAllText(root+"/install-phase","anaconda");
            File.WriteAllText(root+"/packaging.log","layers needed: 128 (5.6 GB)");
            var native="Fetching layers █░ 32/128\n└ Fetching █░ 8.00 MiB/16.00 MiB (2.00 MiB/s) ostree chunk abc";
            File.WriteAllText(root+"/install-download.json",System.Text.Json.JsonSerializer.Serialize(new{layerProgress=native.Split('\n')[0],byteProgress=native.Split('\n')[1]}));
            var downloading=InstallationDiagnostics.Observe(operation,root,"",root);
            check(downloading.Progress?.Detail==native,"Installation progress embeds the downloader's own bar and byte text unchanged");
            File.Delete(root+"/packaging.log");
            check(InstallationDiagnostics.Observe(operation,root,"",root).Progress is {CompletedSteps:2} download&&download.Detail==native,"Native progress identifies download even after packaging logs rotate");
            foreach(var invalid in new[]{"{","null","[]","{\"layerProgress\":\"Fetching layers █ 129/128\"}","{\"layerProgress\":\"Fetching layers █ 0/0\"}","{\"layerProgress\":\"password=secret\"}","{\"layerProgress\":null}","{\"layerProgress\":\"Fetching layers █ 0/128\",\"byteProgress\":null}","{\"layerProgress\":\"Fetching layers █ 0/128\\u001b[2J\"}"})
            {
                File.WriteAllText(root+"/install-download.json",invalid);
                check(InstallationDiagnostics.Observe(operation,root,"",root).Progress?.CompletedSteps==1,"Invalid native snapshots retain stage progress: "+invalid);
            }
            File.WriteAllText(root+"/install-download.json","{\"layerProgress\":\"Fetching layers █ 128/128\",\"byteProgress\":\"└ Fetching █ 8.00 MiB/16.00 MiB (2.00 MiB/s)\"}");
            check(InstallationDiagnostics.Observe(operation,root,"",root).Progress?.Detail=="Fetching layers █ 128/128","Completed native layer bar hides stale per-layer transfer text");
            File.WriteAllText(root+"/packaging.log","Deploying container image...done");
            check(InstallationDiagnostics.Observe(downloading,root,"",root).Progress is {CompletedSteps:3,Detail:""},"Deployment retires native download text without completing installation");
            File.Delete(root+"/packaging.log");File.Delete(root+"/install-download.json");
            File.WriteAllText(root+"/anaconda-output.log","Older output\n\u001b[32mCreating disklabel on /dev/test\u001b[0m\nInstalling boot loader\nConfiguring installed system password=fixture-secret\n");
            var anaconda=InstallationDiagnostics.Observe(operation,root,"",root);
            check(anaconda.Message.Contains("Anaconda:\nCreating disklabel on /dev/test\nInstalling boot loader\nConfiguring installed system password=[REDACTED]")&&!anaconda.Message.Contains("Older output")&&!anaconda.Message.Contains('\u001b'),"Progress embeds bounded recent Anaconda output with credentials and terminal controls removed");
            var observedAgain=InstallationDiagnostics.Observe(anaconda,root,"",root);
            check(observedAgain==anaconda,"Repeated polls do not duplicate native Anaconda status text");
            File.WriteAllText(root+"/anaconda-output.log","Installation complete!\n");
            check(InstallationDiagnostics.Observe(operation,root,"",root).Stage=="Installing","Native completion text cannot replace the successful Xur completion marker");
            File.Delete(root+"/anaconda-output.log");
            var deployed=operation with{Progress=new(3,"Deploy OS and bootloader")};
            check(InstallationDiagnostics.Observe(deployed,root,"",root).Progress?.CompletedSteps==3,"Rotated or truncated logs cannot move the progress bar backwards");
            check(InstallationDiagnostics.FileLogs(root).Contains("No Anaconda log files"),"Pre-Anaconda failure directs users to the service journal");
            File.WriteAllText(root+"/anaconda.log","Older details\nNewest error: fixture\npassword=fixture-secret");
            File.WriteAllText(root+"/xur-post.log","Configuration error: fixture");
            var logs=InstallationDiagnostics.FileLogs(root,2);
            check(logs.Contains("Newest error")&&logs.Contains("Configuration error")&&!logs.Contains("Older details")&&!logs.Contains("fixture-secret"),"Installer diagnostics include bounded redacted Anaconda and configuration log tails");
        }
        finally{Directory.Delete(root,true);}
    }
}
