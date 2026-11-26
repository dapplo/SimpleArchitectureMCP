using DevToolsMcp.Server.Modules.GitHub;

namespace DevToolsMcp.Tests.GitHub;

public class GitHubOptionsTests
{
    [Theory]
    [InlineData(null, "https://api.github.com/")]
    [InlineData("", "https://api.github.com/")]
    [InlineData("https://github.com", "https://api.github.com/")]
    [InlineData("https://api.github.com/", "https://api.github.com/")]
    [InlineData("https://github.example.com", "https://github.example.com/api/v3/")]
    [InlineData("https://github.example.com/api/v3/", "https://github.example.com/api/v3/")]
    [InlineData("https://example.com/git", "https://example.com/git/api/v3/")]
    [InlineData("https://acme.ghe.com", "https://api.acme.ghe.com/")]
    [InlineData("https://api.acme.ghe.com", "https://api.acme.ghe.com/")]
    public void Api_base_address(string? baseUrl, string expected) =>
        Assert.Equal(expected, new GitHubOptions { ApiToken = "t", BaseUrl = baseUrl }.GetApiBaseAddress().ToString());

    [Theory]
    [InlineData(null, null, null, "GitHub:ApiToken not set")]
    [InlineData("t", "ftp://x", null, "GitHub:BaseUrl is not an absolute http(s) URL")]
    [InlineData("t", null, "not a repo", "GitHub:DefaultRepo must look like owner/repo")]
    [InlineData("t", null, "dapplo/SimpleArchitectureMCP", null)]
    [InlineData("t", "https://ghe.corp", null, null)]
    public void Validation(string? token, string? baseUrl, string? defaultRepo, string? reason)
    {
        var ok = new GitHubOptions { ApiToken = token, BaseUrl = baseUrl, DefaultRepo = defaultRepo }.TryValidate(out var actual);
        Assert.Equal(reason is null, ok);
        Assert.Equal(reason, actual);
    }
}
