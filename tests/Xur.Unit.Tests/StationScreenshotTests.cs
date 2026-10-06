using System.Text;
using Xur.Agent;
using Xur.Domain;
using Xur.IO;

static class StationScreenshotTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var recipe=new Recipe("preview-test","Desktop","",[],0,"","",0,0,"",Kind:"Workstation");
        var workstation=new Workload("preview-fixture","Desktop",recipe,[],"preview-test");
        var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABAAAAAJCAIAAAC0SDtlAAAAF0lEQVR4nGN0LJvAQApgIkn1qAZaaQAAQcoBWfCjfbMAAAAASUVORK5CYII=");
        var captureBytes=png;
        var calls=new List<(string Exe,string[] Args)>();
        var session="WAYLAND_DISPLAY=wayland-preview\nHF_TOKEN=private-fixture\n";var uid="15432\n";bool fails=false;
        Task<CommandResult> Run(string exe,string[] args,int seconds,CancellationToken cancellation)
        {
            calls.Add((exe,args));
            return Task.FromResult(exe switch{
                "id"=>new CommandResult(0,Encoding.UTF8.GetBytes(uid),[]),
                "runuser"=>new CommandResult(0,Encoding.UTF8.GetBytes(session),[]),
                _=>fails?new CommandResult(1,[],Encoding.UTF8.GetBytes("Capture failed")):new CommandResult(0,captureBytes,Encoding.UTF8.GetBytes("Qt diagnostic\n"))
            });
        }
        var result=await StationScreenshot.Capture(workstation,runner:Run);
        check(result.SequenceEqual(png),"Desktop capture preserves binary PNG bytes independently of command diagnostics");
        var capture=calls.Single(c=>c.Exe=="systemd-run");
        check(capture.Args.Contains("--property=User="+StationAccounts.Username(workstation))&&capture.Args.Contains("--property=Slice=user-15432.slice")&&!capture.Args.Any(a=>a.Contains("DeviceAllow=")),"Preview captures retain the workstation user's device restrictions");
        check(capture.Args.Contains("--setenv=WAYLAND_DISPLAY=wayland-preview")&&!capture.Args.Any(a=>a.Contains("private-fixture"))&&capture.Args.Contains("--property=RuntimeMaxSec=15"),"Preview uses only the intended Wayland session and bounds capture lifetime");
        async Task Rejected(string name)
        {
            try{await StationScreenshot.Capture(workstation,runner:Run);check(false,name);}
            catch(InvalidOperationException){check(true,name);}
        }
        calls.Clear();session="WAYLAND_DISPLAY=wayland-0;touch /private\n";
        await Rejected("Unsafe Wayland environment is rejected before desktop capture");
        check(!calls.Any(c=>c.Exe=="systemd-run"),"Invalid sessions never launch a capture process");
        session="DISPLAY=:0\n";await Rejected("Missing workstation Wayland session cannot become a successful preview");
        session="WAYLAND_DISPLAY=wayland-preview\n";fails=true;
        await Rejected("Capture failures cannot return an old or empty image");
        fails=false;captureBytes=Encoding.UTF8.GetBytes("Unexpected command output instead of an image");
        await Rejected("Non-image command output is rejected instead of being served as a desktop preview");
        captureBytes=png;uid="0\n";await Rejected("Desktop capture rejects root session identity");
        uid="15432\n";workstation=workstation with{User=new("preview-fixture-user",15433,false)};
        await Rejected("A changed account UID cannot capture another user's desktop");

        // Exercise cleanup and stdout separation with a synthetic capture tool.
        var root=Path.GetFullPath(".build/evidence/workstation-preview/capture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var tool=root+"/spectacle";
            await File.WriteAllTextAsync(tool,"#!/bin/bash\nset -eu\nwhile [ \"$1\" != --output ]; do shift; done\nshift\nprintf diagnostic\\n >&2\nprintf temporary > \"$XDG_CONFIG_HOME/spectaclerc\"\nprintf '"+Convert.ToBase64String(png)+"' | base64 --decode > \"$1\"\n");
            File.SetUnixFileMode(tool,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var script=StationScreenshot.CaptureScript.Replace("/usr/bin/spectacle",tool);
            var captured=await CommandRunner.Run("env",["XDG_RUNTIME_DIR="+root,"RUNTIME_DIRECTORY="+root,"bash","-c",script],5);
            check(captured.ExitCode==0&&captured.Output.SequenceEqual(png)&&captured.Error.Length>0,"Capture helper returns only image bytes without mixing stderr");
            check(!Directory.EnumerateDirectories(root).Any(),"Capture helper removes its screenshot and isolated settings after success");
            await File.WriteAllTextAsync(tool,"#!/bin/bash\nexit 1\n");
            var failed=await CommandRunner.Run("env",["XDG_RUNTIME_DIR="+root,"RUNTIME_DIRECTORY="+root,"bash","-c",script],5);
            check(failed.ExitCode!=0&&failed.Output.Length==0&&!Directory.EnumerateDirectories(root).Any(),"Capture helper removes temporary state after capture failure");
        }
        finally{Directory.Delete(root,true);}
    }
}
