using System.Diagnostics;
using System.Threading.Channels;

namespace Xur.Robot;

public sealed class HandCalibrationAdapter:IHandCalibrationAdapter
{
    public async Task Run(RoboticsConfiguration configuration,Action<HandCalibrationProgress> publish,ChannelReader<string> commands,CancellationToken token)
    {
        var info=new ProcessStartInfo(RoboticsContainer.Python){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(Path.Combine(RoboticsContainer.ToolsDirectory,"bridge.py"));info.ArgumentList.Add("--hand-calibrate");
        using var process=Process.Start(info)??throw new IOException("Could not start hand-guided setup.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(15));
        async Task<string> Errors()
        {
            var text=new System.Text.StringBuilder();var buffer=new char[4096];int count;
            while((count=await process.StandardError.ReadAsync(buffer,deadline.Token))>0)
            {text.Append(buffer,0,count);if(text.Length>8192)text.Remove(0,text.Length-8192);}
            return text.ToString();
        }
        async Task Read()
        {
            while(await process.StandardOutput.ReadLineAsync(deadline.Token) is {} line)
            {
                if(line.Length>32768)throw new IOException("Setup feedback exceeded its limit.");
                publish(RobotJson.Deserialize<HandCalibrationProgress>(line)??throw new IOException("Missing setup feedback."));
            }
        }
        async Task Write()
        {
            await process.StandardInput.WriteLineAsync(RobotJson.Serialize(configuration).AsMemory(),deadline.Token);
            await foreach(var phase in commands.ReadAllAsync(deadline.Token))
                await process.StandardInput.WriteLineAsync(phase.AsMemory(),deadline.Token);
        }
        var errors=Errors();var reading=Read();var writing=Write();var exit=process.WaitForExitAsync(deadline.Token);
        try
        {
            var first=await Task.WhenAny(reading,writing,exit);await first;
            // A broken pipe or reader fault must reach cleanup immediately.
            if(first==writing&&!exit.IsCompleted)throw new IOException("Setup command stream ended unexpectedly.");
            await reading;await exit;
            if(process.ExitCode!=0)throw new InvalidOperationException("Hand-guided setup failed: "+Xur.Domain.Redaction.Logs(await errors));
        }
        finally
        {
            if(!process.HasExited)
            {
                try{await Processes.Run(RoboticsContainer.Python,[Path.Combine(RoboticsContainer.ToolsDirectory,"bridge.py"),"--stop"],5);}catch{}
                try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
                catch(TimeoutException){if(!process.HasExited)process.Kill(entireProcessTree:true);}
            }
            await process.WaitForExitAsync();deadline.Cancel();process.StandardInput.Close();
            try{await Task.WhenAll(reading,writing,errors);}catch{}
        }
    }
}
