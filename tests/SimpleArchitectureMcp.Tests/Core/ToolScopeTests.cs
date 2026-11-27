using System.Text.Json;
using SimpleArchitectureMcp.Server.Core.Modules;
using SimpleArchitectureMcp.Server.Core.Tools;

namespace SimpleArchitectureMcp.Tests.Core;

public class ToolScopeTests
{
    private static ModuleRegistry Registry()
    {
        var r = new ModuleRegistry();
        foreach (var (name, prefix) in new[] { ("jira", "jira_"), ("github", "github_"), ("confluence", "confluence_") })
        {
            r.Add(new ModuleDescriptor { Name = name, Title = name, Section = name, ToolPrefix = prefix, Settings = [], Status = _ => new(true, "") });
        }

        return r;
    }

    [Theory]
    [InlineData(null, "confluence_get_page", false, true)]
    [InlineData("jira,github", "jira_search", true, true)]
    [InlineData("jira,github", "confluence_search", true, false)]
    [InlineData("jira+GitHub", "github_comment", false, true)]
    [InlineData("confluence,-confluence_export_page", "confluence_export_page", false, false)]
    [InlineData("confluence,-confluence_export_page", "confluence_get_page", true, true)]
    [InlineData("-confluence", "jira_search", true, true)]
    [InlineData("-confluence", "confluence_search", true, false)]
    [InlineData("readonly", "jira_search", true, true)]
    [InlineData("readonly", "jira_add_comment", false, false)]
    [InlineData("jira+readonly", "github_get_issue", true, false)]
    [InlineData("jira_search", "jira_search", true, true)]
    [InlineData("jira_search", "jira_get_issue", true, false)]
    public void Scope_selects_modules_tools_and_read_only(string? spec, string tool, bool readOnly, bool expected) =>
        Assert.Equal(expected, ToolScope.Parse(spec).Allows(tool, readOnly, Registry()));

    [Fact]
    public void Tools_argument_becomes_configuration_key()
    {
        Assert.Equal(["--SimpleArchitectureMcp:Tools=jira,github", "x"], ToolScopeArgs.Expand(["--tools", "jira,github", "x"]));
        Assert.Equal(["--SimpleArchitectureMcp:Tools=readonly"], ToolScopeArgs.Expand(["--tools=readonly"]));
    }

    [Fact]
    public void Compactor_drops_null_unions_and_empty_defaults_but_keeps_real_defaults()
    {
        using var doc = JsonDocument.Parse("""
            {"type":"object","properties":{
              "repo":{"description":"owner/repo","type":["string","null"],"default":null},
              "labels":{"type":["array","null"],"items":{"type":["string","null"]},"default":null},
              "inline":{"type":"boolean","default":false},
              "render":{"type":"boolean","default":true},
              "limit":{"type":"integer","default":10},
              "either":{"type":["string","integer","null"]}},
             "required":["repo"]}
            """);
        var compact = ToolSchemaCompactor.Compact(doc.RootElement).GetRawText();
        Assert.Equal("""
            {"type":"object","properties":{"repo":{"description":"owner/repo","type":"string"},"labels":{"type":"array","items":{"type":"string"}},"inline":{"type":"boolean"},"render":{"type":"boolean","default":true},"limit":{"type":"integer","default":10},"either":{"type":["string","integer"]}},"required":["repo"]}
            """.Trim(), compact);
    }
}
