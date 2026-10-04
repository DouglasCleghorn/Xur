using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Xur.Util;

public sealed class UserError(string message) : Exception(message);

/// <summary>Bounded system commands and local health probes; replaceable in fixture tests.</summary>
public class Runtime
{
    public virtual async Task<byte[]> Run(string[] command, int seconds = 60,
        CancellationToken cancellationToken = default, byte[]? input = null)
    {
        var info = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        foreach (var argument in command.Skip(1)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start " + command[0]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
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
            await Task.WhenAll(readOutput, readError);
            throw;
        }
        if (process.ExitCode != 0)
            throw new IOException(command[0] + " failed: " + Encoding.UTF8.GetString(error.ToArray()).Trim());
        return output.ToArray();
    }

    public virtual async Task<JsonObject> Local(string endpoint, string socket,
        CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), token);
                    return new NetworkStream(connection, ownsSocket: true);
                }
                catch { connection.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync("http://localhost" + endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
            ?? throw new IOException("Invalid application health response");
    }

    public virtual Task Delay(CancellationToken cancellationToken) => Task.Delay(1000, cancellationToken);
}
