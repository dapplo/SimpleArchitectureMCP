using ModelContextProtocol.Protocol;

namespace DevToolsMcp.Server.Core.Modules;

public static class ModuleToolGate
{
    /// <summary>
    /// Hides tools of modules that are not (yet) configured, and tools a module reports as unavailable, from
    /// <c>tools/list</c> and rejects calls to them.
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
                var visible = new List<Tool>(result.Tools.Count);
                foreach (var tool in result.Tools)
                {
                    if (await registry.ToolUnavailableAsync(tool.Name, services, ct).ConfigureAwait(false) is null)
                    {
                        visible.Add(tool);
                    }
                }

                result.Tools = visible;
                return result;
            });

            filters.AddCallToolFilter(next => async (context, ct) =>
            {
                var name = context.Params?.Name ?? string.Empty;
                if (await registry.ToolUnavailableAsync(name, context.Services!, ct).ConfigureAwait(false) is not { } reason)
                {
                    return await next(context, ct).ConfigureAwait(false);
                }

                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = reason }] };
            });
        });
    }
}
