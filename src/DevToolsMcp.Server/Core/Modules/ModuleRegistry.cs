using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevToolsMcp.Server.Core.Modules;

/// <summary>
/// Records which integration modules were enabled at startup (for /health and logs).
/// Modules that are not configured register no tools, so they cost zero schema tokens.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly List<string> _enabled = [];
    private readonly List<string> _disabled = [];

    public IReadOnlyList<string> Enabled => _enabled;
    public IReadOnlyList<string> Disabled => _disabled;

    public void MarkEnabled(string module) => _enabled.Add(module);
    public void MarkDisabled(string module, string reason) => _disabled.Add($"{module} ({reason})");

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
