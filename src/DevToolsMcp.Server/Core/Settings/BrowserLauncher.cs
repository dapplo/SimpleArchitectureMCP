using System.Diagnostics;

namespace DevToolsMcp.Server.Core.Settings;

internal static class BrowserLauncher
{
    public static void TryOpen(string url, ILogger logger) =>
        TryOpen(url, message => logger.LogInformation("{Message}", message));

    public static void TryOpen(string url, Action<string> reportFailure)
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
            reportFailure($"Could not open a browser ({ex.Message}). Open {url} manually.");
        }
    }
}
