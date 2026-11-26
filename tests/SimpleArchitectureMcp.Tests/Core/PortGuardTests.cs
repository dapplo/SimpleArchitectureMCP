using System.Net;
using System.Net.Sockets;
using SimpleArchitectureMcp.Server.Core.Security;
using Microsoft.Extensions.Configuration;

namespace SimpleArchitectureMcp.Tests.Core;

public class PortGuardTests
{
    [Fact]
    public async Task Detects_a_port_taken_by_another_program()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(PortGuard.IsFree(port, allowRemote: false));

            // Not a simple-architecture-mcp instance (no /health answer): exit code 1, no browser.
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SimpleArchitectureMcp:OpenBrowser"] = "false" }).Build();
            Assert.Equal(1, await PortGuard.ReportInUseAsync(port, config));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Free_port_is_free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.True(PortGuard.IsFree(port, allowRemote: false));
    }
}
