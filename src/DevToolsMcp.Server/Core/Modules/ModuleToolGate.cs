using ModelContextProtocol.Protocol;

namespace DevToolsMcp.Server.Core.Modules;

public static class ModuleToolGate
{
    /// <summary>
    /// Hides tools of modules that are not (yet) configured from <c>tools/list</c> and rejects calls to them.
    /// Modules can therefore be configured at runtime (settings page) while unconfigured ones cost zero tokens.
    /// </summary>
    public static IMcpServerBuilder WithModuleToolGate(this IMcpServerBuilder mcp)
    {
        var registry = ModuleRegistry.From(mcp.Services);
        return mcp.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct).ConfigureAwait(false);
                var services = context.Services!;
                result.Tools = result.Tools.Where(t => registry.IsToolActive(t.Name, services)).ToList();
                return result;
            });

            filters.AddCallToolFilter(next => (context, ct) =>
            {
                var name = context.Params?.Name ?? string.Empty;
                if (registry.IsToolActive(name, context.Services!))
                {
                    return next(context, ct);
                }

                return ValueTask.FromResult(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"'{name}' is not configured. Open the devtools-mcp settings page to set it up." }],
                });
            });
        });
    }
}
