using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevToolsMcp.Server.Core.Modules;

/// <summary>A configurable setting of a module, rendered by the built-in settings page.</summary>
/// <param name="Kind">text, url, email, secret or select.</param>
public sealed record ModuleSetting(string Key, string Label, string Kind, bool Required = false, string? Help = null, IReadOnlyList<string>? Options = null);

public sealed record ModuleStatus(bool Active, string Message);

/// <summary>
/// Describes an integration module: its configuration section, settings form, live status and tool prefix.
/// Tools of inactive modules are hidden from <c>tools/list</c>, so they cost zero schema tokens.
/// </summary>
public sealed class ModuleDescriptor
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Section { get; init; }
    public required string ToolPrefix { get; init; }
    public required IReadOnlyList<ModuleSetting> Settings { get; init; }

    /// <summary>Evaluated on every request; reflects settings saved at runtime.</summary>
    public required Func<IServiceProvider, ModuleStatus> Status { get; init; }

    /// <summary>
    /// Optional per-tool check for tools of an active module that depend on what the remote system offers (e.g. a
    /// Confluence app). Returns why the tool is unavailable, or null when it can be used. Hidden tools cost no tokens.
    /// </summary>
    public Func<IServiceProvider, string, CancellationToken, ValueTask<string?>>? ToolUnavailable { get; init; }

    /// <summary>Optional connectivity check used by the settings page ("Test connection").</summary>
    public Func<IServiceProvider, CancellationToken, Task<string>>? Test { get; init; }

    /// <summary>Manual conversions offered on the settings page while the module is active (never MCP tools).</summary>
    public IReadOnlyList<ModuleConversion> Conversions { get; init; } = [];
}

/// <summary>All registered modules. Shared by /health, the settings page and the tool gate.</summary>
public sealed class ModuleRegistry
{
    private readonly List<ModuleDescriptor> _modules = [];

    public IReadOnlyList<ModuleDescriptor> Modules => _modules;

    public void Add(ModuleDescriptor module) => _modules.Add(module);

    public ModuleDescriptor? Find(string name) => _modules.Find(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> ActiveModules(IServiceProvider services) =>
        _modules.Where(m => m.Status(services).Active).Select(m => m.Name);

    /// <summary>A tool is visible unless it belongs to a module that is currently inactive.</summary>
    public bool IsToolActive(string toolName, IServiceProvider services)
    {
        foreach (var m in _modules)
        {
            if (toolName.StartsWith(m.ToolPrefix, StringComparison.Ordinal))
            {
                return m.Status(services).Active;
            }
        }

        return true;
    }

    /// <summary>
    /// Null when the tool can be used; otherwise why not (its module is inactive, or the module's
    /// <see cref="ModuleDescriptor.ToolUnavailable"/> check says so).
    /// </summary>
    public async ValueTask<string?> ToolUnavailableAsync(string toolName, IServiceProvider services, CancellationToken ct)
    {
        foreach (var m in _modules)
        {
            if (!toolName.StartsWith(m.ToolPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!m.Status(services).Active)
            {
                return $"'{toolName}' is not configured. Open the devtools-mcp settings page to set it up.";
            }

            return m.ToolUnavailable is null ? null : await m.ToolUnavailable(services, toolName, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Gets the registry singleton from the service collection, adding it on first use.</summary>
    public static ModuleRegistry From(IServiceCollection services)
    {
        foreach (var d in services)
        {
            if (d.ServiceType == typeof(ModuleRegistry) && d.ImplementationInstance is ModuleRegistry existing)
            {
                return existing;
            }
        }

        var registry = new ModuleRegistry();
        services.TryAddSingleton(registry);
        return registry;
    }
}
