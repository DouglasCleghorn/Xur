using System.Diagnostics;
using System.Text.Json;

namespace Xur.Robot;

public interface IRobotTools
{
    Task<JsonElement> Run(RoboticsConfiguration configuration, string operation,
        object? request, int seconds, CancellationToken cancellation);
}

// The subprocess boundary keeps Python limited to the upstream robotics stack.
// Every invocation is a reviewed operation; no shell commands come from clients.
public sealed class LeRobotTools : IRobotTools
{
    public async Task<JsonElement> Run(RoboticsConfiguration configuration, string operation,
        object? request, int seconds, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(RoboticsContainer.Python)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.Combine(RoboticsContainer.ToolsDirectory,"bridge.py"));
        using var process = Process.Start(info) ?? throw new IOException("Could not start the LeRobot adapter.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        async Task<string> ReadBounded(StreamReader reader,bool progress=false)
        {
            var text = new System.Text.StringBuilder(); var buffer = new char[8192]; int count;
            while((count = await reader.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if(!progress && text.Length + count > 8 * 1024 * 1024) throw new IOException("Robot adapter output exceeded its limit.");
                text.Append(buffer, 0, count);
                if(progress && text.Length>65536)text.Remove(0,text.Length-65536);
            }
            return text.ToString();
        }
        var stdout = ReadBounded(process.StandardOutput);
        var stderr = ReadBounded(process.StandardError,progress:true);
        try
        {
            await process.StandardInput.WriteLineAsync(RobotJson.Serialize(new RobotToolRequest(configuration,operation,request==null?null:RobotJson.Element(request))).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            // A failed pipe must also terminate the adapter promptly.
            var pending = new List<Task>{stdout, stderr, process.WaitForExitAsync(deadline.Token)};
            while(pending.Count != 0){var finished = await Task.WhenAny(pending); pending.Remove(finished); await finished;}
            var output = await stdout;
            if(process.ExitCode != 0)
                throw new InvalidOperationException("LeRobot adapter failed: " + Xur.Domain.Redaction.Logs((await stderr)[..Math.Min((await stderr).Length, 2048)]));
            using var result = JsonDocument.Parse(output);
            return result.RootElement.Clone();
        }
        catch
        {
            // Give upstream disconnect/finally handlers time to stop and release
            // the arm. A force kill is only the bounded last step after SIGINT.
            if(!process.HasExited)
            {
                // The bridge records its PID and handles SIGINT locally so
                // upstream finally/disconnect handlers can release the motors.
                try
                {
                    await Processes.Run(RoboticsContainer.Python,[Path.Combine(RoboticsContainer.ToolsDirectory,"bridge.py"),"--stop"],5);
                }
                catch
                {
                    // A failed stop helper must still reach the bounded child
                    // cleanup below and preserve the original operation error.
                }
                try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
                catch(TimeoutException)
                {
                    if(!process.HasExited)process.Kill(entireProcessTree:true);
                }
            }
            await process.WaitForExitAsync();
            deadline.Cancel();
            try{await Task.WhenAll(stdout, stderr);}catch{ }
            throw;
        }
    }

}
