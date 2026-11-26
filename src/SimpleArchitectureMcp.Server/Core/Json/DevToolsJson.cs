using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;

namespace SimpleArchitectureMcp.Server.Core.Json;

/// <summary>
/// Reflection-free JSON composition. Every module contributes its own source-generated
/// <see cref="JsonSerializerContext"/>; they are combined into one resolver chain that is used by
/// ASP.NET Core (minimal APIs) and by the MCP server for tool marshalling.
/// </summary>
public sealed class DevToolsJson
{
    private DevToolsJson(IJsonTypeInfoResolver resolver, JsonSerializerOptions mcpOptions)
    {
        Resolver = resolver;
        McpOptions = mcpOptions;
    }

    /// <summary>The combined resolver of the core and all module contexts.</summary>
    public IJsonTypeInfoResolver Resolver { get; }

    /// <summary>MCP SDK defaults plus all module contexts. Pass to <c>WithTools&lt;T&gt;(options)</c>.</summary>
    public JsonSerializerOptions McpOptions { get; }

    /// <summary>Combines the core context with the given module contexts.</summary>
    public static DevToolsJson Compose(params IJsonTypeInfoResolver[] moduleResolvers)
    {
        var resolver = JsonTypeInfoResolver.Combine([CoreJsonContext.Default, .. moduleResolvers]);

        var mcpOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        mcpOptions.TypeInfoResolverChain.Insert(0, resolver);
        mcpOptions.MakeReadOnly();

        return new DevToolsJson(resolver, mcpOptions);
    }

    /// <summary>Prepends the combined resolver to an existing options chain (e.g. ConfigureHttpJsonOptions).</summary>
    public void ApplyTo(JsonSerializerOptions options) => options.TypeInfoResolverChain.Insert(0, Resolver);
}

/// <summary>Payloads owned by the core plumbing.</summary>
public sealed record HealthResponse(string Status, IReadOnlyList<string> Modules);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(Settings.ModulesView))]
[JsonSerializable(typeof(Settings.ModuleView))]
[JsonSerializable(typeof(Settings.ConversionView))]
[JsonSerializable(typeof(Settings.ResultView))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
