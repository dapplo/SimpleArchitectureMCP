using DevToolsMcp.Server.Modules.Confluence;

namespace DevToolsMcp.Tests.Confluence;

public class ConfluenceCqlTests
{
    [Theory]
    [InlineData("deploy runbook", null, "type = page AND text ~ \"deploy runbook\"")]
    [InlineData("say \"hi\"", null, "type = page AND text ~ \"say \\\"hi\\\"\"")]
    [InlineData("title ~ \"deploy\" ORDER BY lastmodified DESC", null, "title ~ \"deploy\" ORDER BY lastmodified DESC")]
    [InlineData("label in (ops, infra)", null, "label in (ops, infra)")]
    [InlineData("deploy", "OPS", "space = \"OPS\" AND (type = page AND text ~ \"deploy\")")]
    [InlineData("creator = currentUser() order by created", "OPS", "space = \"OPS\" AND (creator = currentUser()) order by created")]
    public void Builds_cql(string query, string? space, string expected) =>
        Assert.Equal(expected, ConfluenceCql.Build(query, space));

    [Fact]
    public void Rejects_injection_through_space_key() =>
        Assert.Throws<ArgumentException>(() => ConfluenceCql.Build("x", "OPS\" OR space = \"HR"));
}
