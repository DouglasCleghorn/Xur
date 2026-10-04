using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Xur.Util;

/// <summary>Runs as the workstation user and addresses only Xur's virtual KDE output.</summary>
public sealed class VirtualDisplay(Runtime runtime)
{
    public const string Output = "Virtual-Xur-Stream";
    async Task<string> Run(string argument, CancellationToken token)
    {
        try { return Encoding.UTF8.GetString(await runtime.Run(["/usr/bin/env", "QT_QPA_PLATFORM=wayland", "/usr/bin/kscreen-doctor", argument], 12, token)); }
        catch (IOException error) { throw new UserError("KDE could not apply the virtual display mode: " + error.Message[^Math.Min(1500, error.Message.Length)..]); }
    }
    public async Task<JsonObject> Observe(CancellationToken token = default)
    {
        var outputs = JsonNode.Parse(await Run("--json", token))?["outputs"] as JsonArray ?? throw new UserError("Invalid KDE display response");
        var matches = outputs.OfType<JsonObject>().Where(o => JsonValues.Text(o["name"]) == Output && JsonValues.Boolean(o["connected"]) && JsonValues.Boolean(o["enabled"])).ToArray();
        if (matches.Length != 1) throw new UserError("Xur's virtual monitor is unavailable. Load a headless workstation first.");
        return matches[0];
    }
    public static void Validate(int width, int height, int fps)
    {
        if (width is < 320 or > 7680 || height is < 240 or > 4320 || width % 2 != 0 || height % 2 != 0 || fps is < 10 or > 240)
            throw new UserError("Use even dimensions from 320×240 to 7680×4320 and 10–240 Hz.");
    }
    static JsonObject? Mode(JsonObject output, int width, int height, int fps) => (output["modes"] as JsonArray ?? [])
        .OfType<JsonObject>().Where(m => JsonValues.Integer(m["size"]?["width"]) == width && JsonValues.Integer(m["size"]?["height"]) == height &&
            m["refreshRate"] is JsonValue rate && rate.TryGetValue<double>(out var refresh) && Math.Abs(refresh - fps) <= Math.Max(.5, fps * .01))
        .OrderBy(m => Math.Abs(m["refreshRate"]!.GetValue<double>() - fps)).FirstOrDefault();
    static string Id(JsonNode? value) => JsonValues.Text(value) ?? JsonValues.Integer(value)?.ToString(CultureInfo.InvariantCulture) ?? "";
    public async Task<JsonObject> Resize(int width, int height, int fps, CancellationToken token = default)
    {
        Validate(width, height, fps);
        var output = await Observe(token);
        var mode = Mode(output, width, height, fps);
        if (mode is null)
        {
            await Run(FormattableString.Invariant($"output.{Output}.addCustomMode.{width}.{height}.{fps * 1000}.full"), token);
            mode = Mode(await Observe(token), width, height, fps);
        }
        if (mode is null) throw new UserError("KDE did not accept this custom mode. The current display was retained.");
        var identity = Id(mode["id"]);
        if (identity.Length == 0 || !identity.All(char.IsAsciiDigit)) throw new UserError("KDE returned an invalid mode identifier.");
        await Run($"output.{Output}.mode.{identity}", token);
        var actual = await Observe(token);
        var selected = Mode(actual, width, height, fps);
        if (selected is null || Id(actual["currentModeId"]) != Id(selected["id"]))
            throw new UserError("KDE did not switch to the requested virtual display mode.");
        return actual;
    }
}
