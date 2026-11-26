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

    /// <summary>
    /// Why the server must not start, or null. Binding all interfaces without <c>McpAuth:ApiKey</c> would let every
    /// machine on the network call the tools with the user's Jira, GitHub and Confluence tokens.
    /// </summary>
    public static string? StartupProblem(IConfiguration config) =>
        AllowRemote(config) && string.IsNullOrWhiteSpace(config["McpAuth:ApiKey"])
            ? "DevToolsMcp:AllowRemote=true makes the tools reachable from other machines, so it needs an API key. " +
              "Set McpAuth:ApiKey (e.g. env McpAuth__ApiKey) and give it to your MCP clients, or remove AllowRemote."
            : null;

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

        var expected = KeyHash(key);
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

    /// <summary>
    /// Keys are compared as SHA-256 hashes: <see cref="CryptographicOperations.FixedTimeEquals"/> returns early on a
    /// length mismatch, so comparing the raw bytes would reveal the key's length.
    /// </summary>
    internal static byte[] KeyHash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

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
            && CryptographicOperations.FixedTimeEquals(KeyHash(presented), expected);
    }

    /// <summary>
    /// Rejects MCP requests a web page could make: with DNS rebinding a site's own host name points at 127.0.0.1, so its
    /// script reaches <c>/mcp</c> "same-origin" and could call every tool with the user's tokens. The MCP spec asks
    /// servers to validate <c>Origin</c> for exactly this. Unless <c>DevToolsMcp:AllowRemote</c> is set, the Host must
    /// be a loopback name; an Origin, when sent (browsers do, MCP clients usually don't), must be a loopback page such
    /// as the MCP Inspector, or the server itself.
    /// </summary>
    public static IApplicationBuilder UseMcpOriginGuard(this WebApplication app)
    {
        var allowRemote = AllowRemote(app.Configuration);
        return app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp") && !IsTrustedMcpRequest(ctx.Request, allowRemote))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Forbidden: requests from web pages (Origin/Host not local) are not accepted.").ConfigureAwait(false);
                return;
            }

            await next(ctx).ConfigureAwait(false);
        });
    }

    internal static bool IsTrustedMcpRequest(HttpRequest request, bool allowRemote)
    {
        if (!allowRemote && !IsLoopbackHost(request.Host.Host))
        {
            return false;
        }

        string? origin = request.Headers.Origin;
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && (IsLoopbackHost(uri.Host) || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>localhost</c> or a loopback IP literal (<c>127.x.x.x</c>, <c>[::1]</c>); never a DNS name that merely resolves there.</summary>
    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
