using System.Diagnostics;
using System.Text.Json;
using Xur.Domain;

namespace Xur.Agent;

public interface IRobotTools
{
    Task<JsonElement> Run(RoboticsConfiguration configuration, string operation,
        object? request, int seconds, CancellationToken cancellation);
}

// The subprocess boundary keeps Python limited to the upstream robotics stack.
// Every invocation is a reviewed operation; no shell commands come from clients.
public sealed class LeRobotTools : IRobotTools
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JsonElement> Run(RoboticsConfiguration configuration, string operation,
        object? request, int seconds, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo("podman")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach(var argument in new[]{"exec","--interactive",RoboticsContainer.Name,"python","/opt/xur/bridge.py"}) info.ArgumentList.Add(argument);
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
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{configuration, operation, request}, Json).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            // A failed pipe must also terminate the adapter promptly.
            var pending = new List<Task>{stdout, stderr, process.WaitForExitAsync(deadline.Token)};
            while(pending.Count != 0){var finished = await Task.WhenAny(pending); pending.Remove(finished); await finished;}
            var output = await stdout;
            if(process.ExitCode != 0)
                throw new InvalidOperationException("LeRobot adapter failed: " + Redaction.Logs((await stderr)[..Math.Min((await stderr).Length, 2048)]));
            using var result = JsonDocument.Parse(output);
            return result.RootElement.Clone();
        }
        catch
        {
            // Give upstream disconnect/finally handlers time to stop and release
            // the arm. A force kill is only the bounded last step after SIGINT.
            if(!process.HasExited)
            {
                // podman exec does not forward host signals reliably. The bridge
                // records its PID and handles this signal inside the container.
                await Processes.Run("podman",["exec",RoboticsContainer.Name,"python","/opt/xur/bridge.py","--stop"],5);
                try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
                catch(TimeoutException)
                {
                    await Processes.Run("podman",["kill",RoboticsContainer.Name],10);
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
