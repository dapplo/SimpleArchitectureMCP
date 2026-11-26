using DevToolsMcp.Server.Core.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Core.Modules;

public static class ModuleToolGate
{
    /// <summary>
    /// Hides tools of modules that are not (yet) configured, and tools a module reports as unavailable, from
    /// <c>tools/list</c> and rejects calls to them.
    /// Modules can therefore be configured at runtime (settings page) while unconfigured ones cost zero tokens.
    /// Also applies the request's <see cref="ToolScope"/> and compacts the schemas (<see cref="ToolSchemaCompactor"/>).
    /// </summary>
    public static IMcpServerBuilder WithModuleToolGate(this IMcpServerBuilder mcp)
    {
        var registry = ModuleRegistry.From(mcp.Services);
        mcp.Services.AddHttpContextAccessor();
        return mcp.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct).ConfigureAwait(false);
                var services = context.Services!;
                var scope = ToolScope.Current(services);
                var visible = new List<Tool>(result.Tools.Count);
                foreach (var tool in result.Tools)
                {
                    if (scope.Allows(tool, registry)
                        && await registry.ToolUnavailableAsync(tool.Name, services, ct).ConfigureAwait(false) is null)
                    {
                        tool.InputSchema = ToolSchemaCompactor.Compact(tool.Name, tool.InputSchema);
                        visible.Add(tool);
                    }
                }

                result.Tools = visible;
                return result;
            });

            filters.AddCallToolFilter(next => async (context, ct) =>
            {
                var name = context.Params?.Name ?? string.Empty;
                var tool = (context.MatchedPrimitive as McpServerTool)?.ProtocolTool;
                if (tool is not null && !ToolScope.Current(context.Services!).Allows(tool, registry))
                {
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"'{name}' is not enabled on this endpoint." }] };
                }

                if (await registry.ToolUnavailableAsync(name, context.Services!, ct).ConfigureAwait(false) is not { } reason)
                {
                    return await next(context, ct).ConfigureAwait(false);
                }

                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = reason }] };
            });
        });
    }
}
