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

    [Theory]
    [InlineData("192.168.1.20", false)]       // another machine, AllowRemote=true, spoofed Host: localhost
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    public void Settings_api_needs_a_connection_from_this_machine(string remote, bool allowed)
    {
        var request = Request("localhost:5180", null);
        request.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remote);
        Assert.Equal(allowed, SettingsEndpoints.IsSameOriginLoopback(request));
    }

    [Fact]
    public void Settings_file_is_private_from_the_moment_it_is_created()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("settings-store").FullName;
        try
        {
            var file = Path.Combine(dir, "settings.json");
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
            new SettingsStore(file, config).Save("GitHub", new Dictionary<string, string?> { ["ApiToken"] = "secret" });

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Contains("secret", File.ReadAllText(file));
            using var probe = new FileStream(Path.Combine(dir, "probe"), SettingsStore.PrivateFile());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "probe")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
