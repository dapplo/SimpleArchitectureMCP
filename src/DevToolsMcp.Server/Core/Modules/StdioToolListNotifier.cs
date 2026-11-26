using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Core.Modules;

/// <summary>
/// In stdio mode the client keeps one session open for as long as it runs. When a settings change turns a module
/// on or off, this sends <c>notifications/tools/list_changed</c>, so clients that support it re-fetch the tool list
/// without a reload.
/// </summary>
public static class StdioToolListNotifier
{
    // Settings reloads also rebuild the module connections from the same change token; let those finish first.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    public static IDisposable Start(IServiceProvider services, IConfiguration configuration, ModuleRegistry registry, ILogger log)
    {
        var server = services.GetRequiredService<McpServer>();
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var gate = new SemaphoreSlim(1, 1);
        var last = Snapshot(registry, services);

        return ChangeToken.OnChange(configuration.GetReloadToken, () => _ = NotifyIfChangedAsync());

        async Task NotifyIfChangedAsync()
        {
            try
            {
                await Task.Delay(Settle, lifetime.ApplicationStopping);
                await gate.WaitAsync(lifetime.ApplicationStopping);
                try
                {
                    var now = Snapshot(registry, services);
                    if (now == last)
                    {
                        return;
                    }

                    last = now;
                    await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, lifetime.ApplicationStopping);
                    log.LogInformation("Active modules changed ({Modules}); told the client to refresh its tool list.", now);
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not send the tool list change notification.");
            }
        }
    }

    // Active modules plus their status line, which also reflects settings that change the tool list (e.g. diagram apps).
    private static string Snapshot(ModuleRegistry registry, IServiceProvider services) =>
        string.Join(",", registry.Modules.Select(m => (m, s: m.Status(services))).Where(x => x.s.Active).Select(x => $"{x.m.Name} ({x.s.Message})"));
}
