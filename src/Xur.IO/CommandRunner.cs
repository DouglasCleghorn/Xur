using System.Diagnostics;

namespace Xur.IO;

public sealed record CommandResult(int ExitCode, byte[] Output, byte[] Error);

/// <summary>Capture stdout separately from stderr and bound the command and its pipe reads.</summary>
public static class CommandRunner
{
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
