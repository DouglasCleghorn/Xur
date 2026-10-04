using System.Text.Json.Nodes;

namespace Xur.Util;

public static class JsonValues
{
    public static string? Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    public static bool Boolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean) && boolean;
    public static bool RequiredBoolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean)
        ? boolean : throw new UserError("Invalid application health response");
    public static long? Integer(JsonNode? value)
    {
        if (value is not JsonValue scalar) return null;
        if (scalar.TryGetValue<long>(out var integer)) return integer;
        return scalar.TryGetValue<int>(out var small) ? small : null;
    }
    public static string RequiredText(JsonNode? value) => Text(value) ?? throw new UserError("Invalid release metadata");
}
