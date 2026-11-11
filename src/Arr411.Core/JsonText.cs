using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Arr411.Core;

/// <summary>JSON written for the web views and the disk: UTF-8 kept as is (messages never end up inside HTML).</summary>
internal static class JsonText
{
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Stringify(JsonNode? node) => node is null ? "null" : node.ToJsonString(Relaxed);

    /// <summary>The string value of a property, or null when it is missing or not a string.</summary>
    public static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
