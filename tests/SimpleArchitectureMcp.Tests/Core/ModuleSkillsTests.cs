using System.Reflection;
using SimpleArchitectureMcp.Server.Core.Modules;
using SimpleArchitectureMcp.Server.Core.Skills;
using ModelContextProtocol.Server;

namespace SimpleArchitectureMcp.Tests.Core;

public class ModuleSkillsTests
{
    private static readonly Assembly Server = typeof(ModuleSkills).Assembly;

    [Fact]
    public void Embedded_skills_load_with_their_required_tools()
    {
        var skills = ModuleSkills.LoadEmbedded(Server);

        Assert.Equal(["archi-to-confluence", "confluence-writing", "migrate-issues"], skills.Select(s => s.Name));
        Assert.Equal(["migrate_issues"], skills.Single(s => s.Name == "migrate-issues").RequiredTools);
        Assert.All(skills, s => Assert.Equal($"skill://{s.Name}/", s.Root));
    }

    [Fact]
    public void Every_required_tool_exists()
    {
        var tools = Server.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet();

        foreach (var skill in ModuleSkills.LoadEmbedded(Server))
        {
            Assert.NotEmpty(skill.RequiredTools);
            Assert.All(skill.RequiredTools, t => Assert.Contains(t, tools));
        }
    }

    [Fact]
    public void Skill_files_are_matched_by_their_root()
    {
        var skills = ModuleSkills.LoadEmbedded(Server);

        Assert.Equal("migrate-issues", ModuleSkills.Find(skills, "skill://migrate-issues/SKILL.md")?.Name);
        Assert.Null(ModuleSkills.Find(skills, "skill://migrate-issues-extra/SKILL.md"));
        Assert.Null(ModuleSkills.Find(skills, "file:///etc/passwd"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("jira,github", true)]
    [InlineData("readonly", true)]
    [InlineData("jira,-skills", false)]
    [InlineData("-Skills", false)]
    public void Scope_can_hide_all_skills(string? spec, bool expected) =>
        Assert.Equal(expected, ToolScope.Parse(spec).AllowsSkills);
}
