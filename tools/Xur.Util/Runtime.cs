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
        var result = await Xur.IO.CommandRunner.Run(command[0], command.Skip(1), seconds, cancellationToken, input);
        if (result.ExitCode != 0) throw new IOException(command[0] + " failed: " + Encoding.UTF8.GetString(result.Error).Trim());
        return result.Output;
    }

    public virtual async Task RunLogged(string[] command, Stream log, int seconds, CancellationToken token = default)
    {
        if (await Xur.IO.CommandRunner.RunLogged(command[0], command.Skip(1), log, seconds, token) != 0)
            throw new IOException(command[0] + " failed; review the update log.");
    }

    public virtual async Task<JsonObject> Local(string endpoint, string socket,
        CancellationToken cancellationToken = default)
    {
        using var client = Xur.IO.HttpClients.Unix(socket, TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync("http://localhost" + endpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
            ?? throw new IOException("Invalid application health response");
    }

    public virtual Task Delay(CancellationToken cancellationToken) => Task.Delay(1000, cancellationToken);
}
