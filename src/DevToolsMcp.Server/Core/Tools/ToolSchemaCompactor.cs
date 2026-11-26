using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevToolsMcp.Server.Core.Tools;

/// <summary>
/// Shrinks the JSON schemas the MCP SDK generates, because every byte of <c>tools/list</c> is paid for on every request.
/// An optional parameter is already optional by not being in <c>required</c>, so the SDK's <c>"type":["string","null"]</c>
/// becomes <c>"type":"string"</c>, and <c>"default":null</c> / <c>"default":false</c> (what an omitted value means
/// anyway) are dropped. Other defaults stay, since they tell the model something. Saves about 10% of the tool list.
/// </summary>
public static class ToolSchemaCompactor
{
    private static readonly ConcurrentDictionary<string, JsonElement> Cache = new(StringComparer.Ordinal);

    /// <summary>The compacted schema of a tool; computed once per tool name (tool schemas never change at runtime).</summary>
    public static JsonElement Compact(string toolName, JsonElement schema) =>
        Cache.GetOrAdd(toolName, static (_, s) => Compact(s), schema);

    public static JsonElement Compact(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        Strip(node);
        using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "{}");
        return doc.RootElement.Clone();
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonArray types && types.Any(IsNull))
                {
                    var rest = types.Where(t => !IsNull(t)).Select(t => t!.GetValue<string>()).ToList();
                    obj["type"] = rest.Count == 1 ? JsonValue.Create(rest[0]) : new JsonArray([.. rest.Select(r => (JsonNode)JsonValue.Create(r))]);
                }

                if (obj.TryGetPropertyValue("default", out var d) && (d is null || d.GetValueKind() == JsonValueKind.False))
                {
                    obj.Remove("default");
                }

                foreach (var (_, child) in obj.ToList())
                {
                    Strip(child);
                }

                break;
            case JsonArray arr:
                foreach (var child in arr)
                {
                    Strip(child);
                }

                break;
        }
    }

    private static bool IsNull(JsonNode? t) => t is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "null";
}
