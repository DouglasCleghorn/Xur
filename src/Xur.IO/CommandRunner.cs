using System.Diagnostics;

namespace Xur.IO;

public sealed record CommandResult(int ExitCode, byte[] Output, byte[] Error);

/// <summary>Capture stdout separately from stderr and bound the command and its pipe reads.</summary>
public static class CommandRunner
{
    public static async Task<int> RunLogged(string executable, IEnumerable<string> arguments, Stream log,
        int seconds = 30, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + executable);
        using var gate = new SemaphoreSlim(1);
        async Task Pump(Stream source)
        {
            var buffer = new byte[65536]; int count;
            while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
            {
                await gate.WaitAsync(deadline.Token);
                try { await log.WriteAsync(buffer.AsMemory(0, count), deadline.Token); await log.FlushAsync(deadline.Token); }
                finally { gate.Release(); }
            }
        }
        var stdout = Pump(process.StandardOutput.BaseStream);
        var stderr = Pump(process.StandardError.BaseStream);
        var pipes = Task.WhenAll(stdout, stderr);
        try
        {
            // Observe either pipe's failure immediately, even while the other pipe is waiting.
            var pending = new List<Task> { process.WaitForExitAsync(deadline.Token), stdout, stderr };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                await completed;
            }
        }
        catch
        {
            deadline.Cancel();
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await pipes; } catch { }
            throw;
        }
        return process.ExitCode;
    }
    public static async Task<CommandResult> Run(string executable, IEnumerable<string> arguments,
        int seconds = 30, CancellationToken cancellationToken = default, byte[]? input = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + executable);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output, deadline.Token);
        var readError = process.StandardError.BaseStream.CopyToAsync(error, deadline.Token);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(readOutput, readError);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(readOutput, readError); } catch { }
            throw;
        }
        return new(process.ExitCode, output.ToArray(), error.ToArray());
    }
}
