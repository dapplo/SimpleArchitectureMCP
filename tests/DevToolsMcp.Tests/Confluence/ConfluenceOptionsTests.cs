using DevToolsMcp.Server.Modules.Confluence;

namespace DevToolsMcp.Tests.Confluence;

public class ConfluenceOptionsTests
{
    [Theory]
    [InlineData("https://acme.atlassian.net", null, ConfluenceDeploymentType.Auto, ConfluenceDeploymentType.Cloud)]
    [InlineData("https://acme.atlassian.net/wiki", "a@b.c", ConfluenceDeploymentType.Auto, ConfluenceDeploymentType.Cloud)]
    [InlineData("https://wiki.corp.local/confluence", "a@b.c", ConfluenceDeploymentType.Auto, ConfluenceDeploymentType.Cloud)]
    [InlineData("https://wiki.corp.local/confluence", null, ConfluenceDeploymentType.Auto, ConfluenceDeploymentType.DataCenter)]
    [InlineData("https://acme.atlassian.net", "a@b.c", ConfluenceDeploymentType.DataCenter, ConfluenceDeploymentType.DataCenter)]
    public void Resolves_deployment_type(string url, string? email, ConfluenceDeploymentType configured, ConfluenceDeploymentType expected)
    {
        var options = new ConfluenceOptions { BaseUrl = url, Email = email, ApiToken = "t", DeploymentType = configured };
        Assert.Equal(expected, options.ResolveDeploymentType());
    }

    [Theory]
    [InlineData("https://acme.atlassian.net/wiki/", ConfluenceDeploymentType.Cloud, "https://acme.atlassian.net/")]
    [InlineData("https://acme.atlassian.net", ConfluenceDeploymentType.Cloud, "https://acme.atlassian.net/")]
    [InlineData("https://wiki.corp.local/confluence/", ConfluenceDeploymentType.DataCenter, "https://wiki.corp.local/confluence/")]
    [InlineData("https://wiki.corp.local", ConfluenceDeploymentType.DataCenter, "https://wiki.corp.local/")]
    public void Base_address_respects_context_path(string url, ConfluenceDeploymentType type, string expected)
    {
        var options = new ConfluenceOptions { BaseUrl = url, ApiToken = "t" };
        Assert.Equal(expected, options.GetApiBaseAddress(type).ToString());
    }

    [Fact]
    public void Auth_scheme_per_deployment()
    {
        var cloud = new ConfluenceOptions { BaseUrl = "https://a.atlassian.net", Email = "me@x.io", ApiToken = "tok" };
        var dc = new ConfluenceOptions { BaseUrl = "https://wiki.local", ApiToken = "pat" };

        var basic = cloud.CreateAuthorization(ConfluenceDeploymentType.Cloud);
        Assert.Equal("Basic", basic.Scheme);
        Assert.Equal("me@x.io:tok", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(basic.Parameter!)));

        var bearer = dc.CreateAuthorization(ConfluenceDeploymentType.DataCenter);
        Assert.Equal("Bearer", bearer.Scheme);
        Assert.Equal("pat", bearer.Parameter);
    }

    [Theory]
    [InlineData(null, "t", null)]
    [InlineData("https://x", null, null)]
    [InlineData("not a url", "t", null)]
    [InlineData("https://a.atlassian.net", "t", null)] // Cloud needs an email
    public void Invalid_configuration_is_rejected(string? url, string? token, string? email)
    {
        var options = new ConfluenceOptions { BaseUrl = url, ApiToken = token, Email = email };
        Assert.False(options.TryValidate(out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }
}
