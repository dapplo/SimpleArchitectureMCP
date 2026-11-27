using System.Reflection;
using System.Text.Json.Nodes;
using SimpleArchitectureMcp.Server.Core.Modules;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SimpleArchitectureMcp.Server.Core.Skills;

/// <summary>A built-in skill and the tools it needs. It is offered only while all of those tools are visible.</summary>
public sealed record ModuleSkill(McpServerSkillPackage Package, IReadOnlyList<string> RequiredTools)
{
    public string Name => Package.Skill.ProtocolSkill?.Name ?? string.Empty;

    /// <summary><c>skill://name/</c>: the prefix of the URIs of all of this skill's files.</summary>
    public string Root => Package.Skill.Id[..(Package.Skill.Id.Length - SkillsProtocol.SkillFileName.Length)];
}

/// <summary>
/// Serves the workflow skills embedded under <c>Skills/</c> over the MCP Skills extension (<c>skills/list</c>, files
/// via <c>resources/read</c>). Clients load only names and descriptions until a skill is used, so a skill costs a
/// line of context until then. A skill names the tools it drives in its frontmatter (<c>metadata.requires</c>) and
/// is hidden whenever one of them is: unconfigured module, endpoint scope (<c>/mcp/jira</c>), read-only scope.
/// <c>-skills</c> in a scope hides all skills.
/// </summary>
public static class ModuleSkills
{
    private const string EmbeddedPrefix = "skills/";

    public static IMcpServerBuilder WithModuleSkills(this IMcpServerBuilder mcp) =>
        mcp.WithModuleSkills(LoadEmbedded(typeof(ModuleSkills).Assembly));

    internal static IMcpServerBuilder WithModuleSkills(this IMcpServerBuilder mcp, IReadOnlyList<ModuleSkill> skills)
    {
        var registry = ModuleRegistry.From(mcp.Services);
        mcp.Services.AddHttpContextAccessor();
        mcp.WithSkillPackages(skills.Select(s => s.Package));
        mcp.WithSkills(o =>
        {
            o.ListSkillsFilters.Add(next => async (context, ct) =>
            {
                var result = await next(context, ct).ConfigureAwait(false);
                var visible = new List<Skill>(result.Skills.Count);
                foreach (var skill in result.Skills)
                {
                    if (Find(skills, skill.Uri) is not { } s || await IsVisibleAsync(s, context.Server, context.Services!, registry, ct).ConfigureAwait(false))
                    {
                        visible.Add(skill);
                    }
                }

                result.Skills = visible;
                return result;
            });

            o.GetSkillFilters.Add(next => async (context, ct) =>
            {
                await ThrowIfHiddenAsync(skills, context.Params?.Uri, context.Server, context.Services!, registry, ct).ConfigureAwait(false);
                return await next(context, ct).ConfigureAwait(false);
            });
        });

        return mcp.WithRequestFilters(filters =>
        {
            // Skill files are read through skills/list, which carries their manifest; they need not be in resources/list.
            filters.AddListResourcesFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct).ConfigureAwait(false);
                result.Resources = [.. result.Resources.Where(r => Find(skills, r.Uri) is null)];
                return result;
            });

            filters.AddReadResourceFilter(next => async (context, ct) =>
            {
                await ThrowIfHiddenAsync(skills, context.Params?.Uri, context.Server, context.Services!, registry, ct).ConfigureAwait(false);
                return await next(context, ct).ConfigureAwait(false);
            });
        });
    }

    /// <summary>Reads skills embedded as <c>skills/&lt;name&gt;/&lt;path&gt;</c> (see the csproj).</summary>
    internal static IReadOnlyList<ModuleSkill> LoadEmbedded(Assembly assembly)
    {
        var byName = new SortedDictionary<string, List<McpServerSkillFile>>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var path = resource.Replace('\\', '/');
            var slash = path.StartsWith(EmbeddedPrefix, StringComparison.Ordinal) ? path.IndexOf('/', EmbeddedPrefix.Length) : -1;
            if (slash < 0)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var name = path[EmbeddedPrefix.Length..slash];
            if (!byName.TryGetValue(name, out var files))
            {
                byName[name] = files = [];
            }

            files.Add(new McpServerSkillFile { Path = path[(slash + 1)..], Content = buffer.ToArray() });
        }

        return [.. byName.Values.Select(files => Create(McpServerSkillPackage.Create(files)))];
    }

    internal static ModuleSkill Create(McpServerSkillPackage package)
    {
        var requires = package.Skill.ProtocolSkill?.Frontmatter?["metadata"] is JsonObject meta && meta["requires"] is JsonValue v
                       && v.TryGetValue(out string? list) && list is not null
            ? list.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            : [];
        return new ModuleSkill(package, requires);
    }

    internal static ModuleSkill? Find(IReadOnlyList<ModuleSkill> skills, string? uri) =>
        uri is null ? null : skills.FirstOrDefault(s => uri.StartsWith(s.Root, StringComparison.Ordinal));

    /// <summary>Visible when skills aren't excluded by the request's scope and every required tool is visible.</summary>
    internal static async ValueTask<bool> IsVisibleAsync(ModuleSkill skill, McpServer server, IServiceProvider services, ModuleRegistry registry, CancellationToken ct)
    {
        var scope = ToolScope.Current(services);
        if (!scope.AllowsSkills)
        {
            return false;
        }

        var tools = server.ServerOptions.ToolCollection;
        foreach (var name in skill.RequiredTools)
        {
            if (tools is null || !tools.TryGetPrimitive(name, out var tool) || !scope.Allows(tool.ProtocolTool, registry)
                || await registry.ToolUnavailableAsync(name, services, ct).ConfigureAwait(false) is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static async ValueTask ThrowIfHiddenAsync(IReadOnlyList<ModuleSkill> skills, string? uri, McpServer server, IServiceProvider services,
        ModuleRegistry registry, CancellationToken ct)
    {
        if (Find(skills, uri) is { } skill && !await IsVisibleAsync(skill, server, services, registry, ct).ConfigureAwait(false))
        {
            // Same answer as for a skill that doesn't exist, so a hidden skill isn't distinguishable from none.
            throw new McpProtocolException($"No skill is served at {uri}", McpErrorCode.InvalidParams);
        }
    }
}
