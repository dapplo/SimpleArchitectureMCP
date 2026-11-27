using System.Net;
using System.Net.Sockets;
using SimpleArchitectureMcp.Server.Core.Settings;

namespace SimpleArchitectureMcp.Server.Core.Security;

/// <summary>
/// Handles a second start (e.g. double-clicking the exe again) before Kestrel tries to bind: without this the
/// host aborts with a long, symbol-less Native AOT stack trace.
/// </summary>
public static class PortGuard
{
    /// <summary>True when nothing listens on the port (loopback, or all interfaces when <paramref name="allowRemote"/>).</summary>
    public static bool IsFree(int port, bool allowRemote)
    {
        try
        {
            var listener = new TcpListener(allowRemote ? IPAddress.Any : IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Explains why the port is taken. If it is another simple-architecture-mcp instance, opens its settings page (unless
    /// <c>SimpleArchitectureMcp:OpenBrowser=false</c>) and returns exit code 0; otherwise returns 1.
    /// </summary>
    public static async Task<int> ReportInUseAsync(int port, IConfiguration config)
    {
        var baseUrl = $"http://localhost:{port}/";
        if (await IsSimpleArchitectureMcpAsync(port))
        {
            Console.Error.WriteLine($"simple-architecture-mcp is already running. MCP endpoint: {baseUrl}mcp  Settings: {baseUrl}");
            if (!string.Equals(config["SimpleArchitectureMcp:OpenBrowser"], "false", StringComparison.OrdinalIgnoreCase))
            {
                BrowserLauncher.TryOpen(baseUrl, message => Console.Error.WriteLine(message));
            }

            return 0;
        }

        Console.Error.WriteLine(
            $"Port {port} is already used by another program. Start simple-architecture-mcp on another port, e.g. " +
            $"'simple-architecture-mcp --SimpleArchitectureMcp:Port={(port == 65535 ? 5181 : port + 1)}' or set SimpleArchitectureMcp__Port.");
        return 1;
    }

    private static async Task<bool> IsSimpleArchitectureMcpAsync(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/health");
            return response.IsSuccessStatusCode
                && (await response.Content.ReadAsStringAsync()).Contains("\"status\":\"ok\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
