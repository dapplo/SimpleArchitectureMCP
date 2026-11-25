using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DevToolsMcp.Server.Core.Security;

public static class LocalHosting
{
    public const int DefaultPort = 5180;

    /// <summary>
    /// Binds Kestrel to loopback only (127.0.0.1 and ::1). The port comes from <c>DevToolsMcp:Port</c>,
    /// then <c>PORT</c> (set by Aspire), then 5180. Binding all interfaces requires <c>DevToolsMcp:AllowRemote=true</c>.
    /// </summary>
    public static int ConfigureLocalKestrel(this WebApplicationBuilder builder) =>
        builder.ConfigureLocalKestrel(ResolvePort(builder.Configuration));

    /// <summary>The configured port: <c>DevToolsMcp:Port</c>, then <c>PORT</c>, then 5180.</summary>
    public static int ResolvePort(IConfiguration config) =>
        ParsePort(config["DevToolsMcp:Port"]) ?? ParsePort(config["PORT"]) ?? DefaultPort;

    public static bool AllowRemote(IConfiguration config) =>
        string.Equals(config["DevToolsMcp:AllowRemote"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Binds Kestrel to <paramref name="port"/>; 0 picks a free loopback port.</summary>
    public static int ConfigureLocalKestrel(this WebApplicationBuilder builder, int port)
    {
        var allowRemote = AllowRemote(builder.Configuration);

        builder.WebHost.ConfigureKestrel(k =>
        {
            if (allowRemote)
            {
                k.Listen(IPAddress.Any, port);
            }
            else if (port == 0)
            {
                k.Listen(IPAddress.Loopback, 0); // ListenLocalhost does not support dynamic ports
            }
            else
            {
                k.ListenLocalhost(port);
            }
        });

        return port;
    }

    private static int? ParsePort(string? value) =>
        int.TryParse(value, out var p) && p is > 0 and <= 65535 ? p : null;

    /// <summary>
    /// When <c>McpAuth:ApiKey</c> is configured, requires <c>Authorization: Bearer &lt;key&gt;</c> (or <c>X-API-Key</c>)
    /// on every request except <c>/health</c> and the static settings page shell (<c>GET /</c>); the page's API calls
    /// still need the key.
    /// </summary>
    public static IApplicationBuilder UseOptionalApiKey(this WebApplication app)
    {
        var key = app.Configuration["McpAuth:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            return app;
        }

        var expected = Encoding.UTF8.GetBytes(key);
        return app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/health") || (path == "/" && HttpMethods.IsGet(ctx.Request.Method)) || IsAuthorized(ctx.Request, expected))
            {
                await next(ctx);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
        });
    }

    internal static bool IsAuthorized(HttpRequest request, byte[] expected)
    {
        string? presented = request.Headers["X-API-Key"];
        if (string.IsNullOrEmpty(presented))
        {
            string? auth = request.Headers.Authorization;
            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                presented = auth["Bearer ".Length..].Trim();
            }
        }

        return !string.IsNullOrEmpty(presented)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expected);
    }
}
