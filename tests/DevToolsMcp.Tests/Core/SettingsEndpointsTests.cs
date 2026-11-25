using DevToolsMcp.Server.Core.Settings;
using Microsoft.AspNetCore.Http;

namespace DevToolsMcp.Tests.Core;

public class SettingsEndpointsTests
{
    private static HttpRequest Request(string host, string? origin)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString(host);
        if (origin is not null)
        {
            ctx.Request.Headers.Origin = origin;
        }

        return ctx.Request;
    }

    [Theory]
    [InlineData("localhost:5180", null, true)]
    [InlineData("127.0.0.1:5180", "http://127.0.0.1:5180", true)]
    [InlineData("[::1]:5180", null, true)]
    [InlineData("localhost:5180", "https://evil.example", false)]   // cross-site request
    [InlineData("evil.example:5180", null, false)]                  // DNS rebinding
    public void Only_same_origin_loopback_requests_are_allowed(string host, string? origin, bool allowed) =>
        Assert.Equal(allowed, SettingsEndpoints.IsSameOriginLoopback(Request(host, origin)));
}
