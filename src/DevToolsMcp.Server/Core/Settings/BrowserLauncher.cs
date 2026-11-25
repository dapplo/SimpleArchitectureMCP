using System.Diagnostics;

namespace DevToolsMcp.Server.Core.Settings;

internal static class BrowserLauncher
{
    public static void TryOpen(string url, ILogger logger)
    {
        try
        {
            var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", url)
                : new ProcessStartInfo("xdg-open", url);
            psi.RedirectStandardError = !OperatingSystem.IsWindows();
            psi.RedirectStandardOutput = !OperatingSystem.IsWindows();
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            logger.LogInformation("Could not open a browser ({Reason}). Open {Url} manually.", ex.Message, url);
        }
    }
}
