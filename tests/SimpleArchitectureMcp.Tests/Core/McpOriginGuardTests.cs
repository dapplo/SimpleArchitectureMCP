using SimpleArchitectureMcp.Server.Core.Security;
using Microsoft.AspNetCore.Http;

namespace SimpleArchitectureMcp.Tests.Core;

public class McpOriginGuardTests
{
    private static HttpRequest Request(string host, string? origin, string? apiKey = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString(host);
        if (origin is not null)
        {
            ctx.Request.Headers.Origin = origin;
        }

        if (apiKey is not null)
        {
            ctx.Request.Headers["X-API-Key"] = apiKey;
        }

        return ctx.Request;
    }

    [Theory]
    [InlineData("localhost:5180", null, true)]                           // MCP clients send no Origin
    [InlineData("127.0.0.1:5180", null, true)]
    [InlineData("[::1]:5180", null, true)]
    [InlineData("localhost:5180", "http://localhost:6274", true)]       // MCP Inspector
    [InlineData("localhost:5180", "http://127.0.0.1:5180", true)]
    [InlineData("evil.example:5180", null, false)]                       // DNS rebinding: Host is the attacker's name
    [InlineData("evil.example:5180", "http://evil.example:5180", false)]
    [InlineData("localhost:5180", "https://evil.example", false)]       // cross-site fetch
    [InlineData("localhost:5180", "http://evil.localhost", false)]
    [InlineData("localhost:5180", "null", false)]                       // sandboxed iframe / file:// page
    public void Mcp_endpoint_refuses_web_pages(string host, string? origin, bool allowed) =>
        Assert.Equal(allowed, LocalHosting.IsTrustedMcpRequest(Request(host, origin), allowRemote: false));

    [Theory]
    [InlineData("devbox:5180", null, true)]                              // AllowRemote: any host name (the API key guards it)
    [InlineData("devbox:5180", "http://devbox:5180", true)]
    [InlineData("devbox:5180", "https://evil.example", false)]
    public void Remote_mode_still_refuses_foreign_origins(string host, string? origin, bool allowed) =>
        Assert.Equal(allowed, LocalHosting.IsTrustedMcpRequest(Request(host, origin), allowRemote: true));

    [Theory]
    [InlineData("s3cret-key", true)]
    [InlineData("s3cret-ke", false)]
    [InlineData("s3cret-key-and-more", false)]
    [InlineData("", false)]
    public void Api_key_must_match_exactly(string presented, bool allowed) =>
        Assert.Equal(allowed, LocalHosting.IsAuthorized(Request("localhost:5180", null, presented), LocalHosting.KeyHash("s3cret-key")));

    [Fact]
    public void Api_key_is_accepted_as_bearer_token()
    {
        var request = Request("localhost:5180", null);
        request.Headers.Authorization = "Bearer s3cret-key";
        Assert.True(LocalHosting.IsAuthorized(request, LocalHosting.KeyHash("s3cret-key")));
        Assert.Equal(32, LocalHosting.KeyHash(new string('x', 500)).Length); // compared length never depends on the key
    }
}
