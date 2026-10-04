using System.Text;
using System.Text.Json.Nodes;

namespace Xur.Util;

/// <summary>Independent sequential updates. An OS failure does not skip application updates.</summary>
public sealed class UpdateAll(string root, string host, Runtime runtime, DurableFiles files,
    Func<string, string, CancellationToken, Task<JsonObject?>>? invoke = null)
{
    string State => Path.Combine(root, "operation.json");
    string Lock => Path.Combine(root, "lock");
    public JsonObject Status()
    {
        var operation = DurableFiles.ReadJson(State)?.DeepClone();
        var busy = false;
        if (File.Exists(Lock))
        {
            try { using var locked = Linux.Lock(Lock); }
            catch (UserError) { busy = true; }
        }
        if (operation is JsonObject obj && JsonValues.Text(obj["stage"]) == "Running" && !busy)
        {
            obj["stage"] = "Interrupted";
            obj["message"] = "Update All was interrupted. Review Updates before trying again.";
        }
        return new() { ["busy"] = busy, ["operation"] = operation };
    }
    async Task<JsonObject?> Invoke(string tool, string action, CancellationToken token)
    {
        if (invoke is not null) return await invoke(tool, action, token);
        // Resolve once at job startup: an app update may change the current link.
        var command = tool == "app-update" ? new[] { Path.Combine(host, "xurutil"), "app-update", action }
            : new[] { "/usr/bin/python3", Path.Combine(host, tool), action };
        try
        {
            var output = await runtime.Run(command, tool == "os-update" ? 7500 : 3700, token);
            return action == "status" ? JsonNode.Parse(Encoding.UTF8.GetString(output)) as JsonObject ?? throw new IOException("Invalid update status") : null;
        }
        catch (IOException) { throw new UserError("Operation failed; see its log on the Updates page."); }
    }
    public async Task<JsonObject> Execute(CancellationToken token = default)
    {
        var operation = new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["stage"] = "Running", ["results"] = new JsonArray() };
        void Progress(string message)
        {
            operation["message"] = message;
            operation["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            files.WriteJson(State, operation);
        }
        Progress("Updating operating system");
        foreach (var (name, tool) in new[] { ("Operating system", "os-update"), ("Xur", "app-update") })
        {
            string message, outcome;
            try
            {
                Progress("Checking " + name);
                var state = await Invoke(tool, "status", token) ?? throw new IOException("Missing update status");
                if (JsonValues.Boolean(state["busy"])) throw new UserError("Another update is already running. Try again after it finishes.");
                if (tool == "os-update" && (state["pending"] is not null || JsonValues.Boolean(state["rollbackQueued"])))
                { message = "An OS deployment is already queued. Reboot to finish."; outcome = "Skipped"; }
                else if (tool == "app-update" && string.IsNullOrEmpty(JsonValues.Text(state["server"])))
                { message = "Set an update server on the Updates page."; outcome = "Skipped"; }
                else
                {
                    await Invoke(tool, "check", token);
                    state = await Invoke(tool, "status", token) ?? throw new IOException("Missing update status");
                    var key = tool == "os-update" ? "digest" : "id";
                    if (state["available"] is JsonObject available && JsonValues.RequiredText(available[key]) != JsonValues.RequiredText(state["current"]?[key]))
                    {
                        var expected = JsonValues.RequiredText(available[key]);
                        Progress("Updating " + name);
                        await Invoke(tool, tool == "os-update" ? "stage" : "update", token);
                        state = await Invoke(tool, "status", token) ?? throw new IOException("Missing update status");
                        if (tool == "os-update" && state["pending"] is null && JsonValues.Text(state["current"]?[key]) != expected)
                            throw new UserError("The OS update did not stage a deployment. Review the operating system update log.");
                        message = state["pending"] is not null ? "Reboot to finish." : "Updated.";
                    }
                    else message = "Up to date.";
                    outcome = "Complete";
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            { outcome = "Failed"; message = error is UserError ? error.Message : "Update failed; see system logs."; }
            ((JsonArray)operation["results"]!).Add((JsonNode)new JsonObject { ["name"] = name, ["stage"] = outcome, ["message"] = message });
            Progress(name + " finished");
        }
        var failed = ((JsonArray)operation["results"]!).Any(row => JsonValues.Text(row?["stage"]) == "Failed");
        operation["stage"] = failed ? "Failed" : "Complete";
        Progress(failed ? "Finished with errors." : "Update checks finished.");
        return operation;
    }
}
