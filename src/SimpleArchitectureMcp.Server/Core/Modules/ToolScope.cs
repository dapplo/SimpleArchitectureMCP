using ModelContextProtocol.Protocol;

namespace SimpleArchitectureMcp.Server.Core.Modules;

/// <summary>
/// Which tools one client sees, so a client that only needs Jira doesn't pay for the Confluence schemas.
/// A spec is a comma-separated list: module names (<c>jira</c>) and tool names (<c>confluence_get_page</c>) to show,
/// <c>-name</c> to hide one, and <c>readonly</c> for read-only tools only. No names to show means all tools.
/// Examples: <c>jira,github</c>, <c>confluence,-confluence_export_page</c>, <c>readonly</c>.
/// It comes from the endpoint path (<c>/mcp/jira,github</c>), otherwise from <c>SimpleArchitectureMcp:Tools</c>
/// (<c>--tools jira,github</c> for stdio). Skills follow the tools they need; <c>-skills</c> hides them all.
/// </summary>
public sealed class ToolScope
{
    public const string ConfigKey = "SimpleArchitectureMcp:Tools";
    public const string RouteKey = "tools";

    public static readonly ToolScope All = new([], [], readOnly: false);

    private readonly HashSet<string> _include;
    private readonly HashSet<string> _exclude;
    private readonly bool _readOnly;

    private ToolScope(HashSet<string> include, HashSet<string> exclude, bool readOnly)
    {
        _include = include;
        _exclude = exclude;
        _readOnly = readOnly;
    }

    public static ToolScope Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return All;
        }

        var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readOnly = false;
        foreach (var raw in spec.Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(raw, "readonly", StringComparison.OrdinalIgnoreCase))
            {
                readOnly = true;
            }
            else if (raw.StartsWith('-') || raw.StartsWith('!'))
            {
                if (raw.Length > 1)
                {
                    exclude.Add(raw[1..]);
                }
            }
            else
            {
                include.Add(raw);
            }
        }

        return new ToolScope(include, exclude, readOnly);
    }

    /// <summary>The scope of the current request: the endpoint path's, else the configured default.</summary>
    public static ToolScope Current(IServiceProvider services)
    {
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (http?.Request.RouteValues.TryGetValue(RouteKey, out var fromPath) == true && fromPath is string path)
        {
            return Parse(Uri.UnescapeDataString(path));
        }

        return Parse(services.GetService<IConfiguration>()?[ConfigKey]);
    }

    /// <summary>False when the scope hides all skills (<c>-skills</c>).</summary>
    public bool AllowsSkills => !_exclude.Contains(SkillsKey);

    public const string SkillsKey = "skills";

    public bool Allows(Tool tool, ModuleRegistry registry) =>
        Allows(tool.Name, tool.Annotations?.ReadOnlyHint == true, registry);

    public bool Allows(string toolName, bool readOnly, ModuleRegistry registry)
    {
        if (ReferenceEquals(this, All))
        {
            return true;
        }

        var module = registry.Modules.FirstOrDefault(m => toolName.StartsWith(m.ToolPrefix, StringComparison.Ordinal))?.Name;
        if (_exclude.Contains(toolName) || (module is not null && _exclude.Contains(module)) || (_readOnly && !readOnly))
        {
            return false;
        }

        return _include.Count == 0 || _include.Contains(toolName) || (module is not null && _include.Contains(module));
    }
}

/// <summary>Turns the short <c>--tools x</c> / <c>--tools=x</c> into the configuration key <see cref="ToolScope.ConfigKey"/>.</summary>
public static class ToolScopeArgs
{
    public static string[] Expand(string[] args)
    {
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--tools", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                result.Add($"--{ToolScope.ConfigKey}={args[++i]}");
            }
            else if (a.StartsWith("--tools=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add($"--{ToolScope.ConfigKey}={a["--tools=".Length..]}");
            }
            else
            {
                result.Add(a);
            }
        }

        return [.. result];
    }
}
