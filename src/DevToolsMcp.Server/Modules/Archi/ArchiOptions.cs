namespace DevToolsMcp.Server.Modules.Archi;

/// <summary>Bound from the <c>Archi</c> configuration section (env: <c>Archi__ExecutablePath</c>, ...).</summary>
public sealed class ArchiOptions
{
    public const string SectionName = "Archi";
    public const string HtmlReport = "HtmlReport";
    public const string JArchi = "jArchi";

    /// <summary>Archi.exe (Windows), Archi (Linux) or Archi.app (macOS). Required.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// HtmlReport (default): stock Archi, the PNGs of its HTML report (100% scale).
    /// jArchi: a generated jArchi script renders only the requested views, with <see cref="Scale"/> and <see cref="Margin"/>.
    /// </summary>
    public string? Renderer { get; set; }

    /// <summary>Where PNGs and generated models go when a tool call names no path. Default: %TEMP%/devtools-mcp/archi.</summary>
    public string? OutputDirectory { get; set; }

    public string? TimeoutSeconds { get; set; }

    /// <summary>jArchi only: 50-400 (percent).</summary>
    public string? Scale { get; set; }

    /// <summary>jArchi only: border in pixels around the diagram.</summary>
    public string? Margin { get; set; }

    public bool UseJArchi => string.Equals(Renderer?.Trim(), JArchi, StringComparison.OrdinalIgnoreCase);

    public int Timeout => Clamp(TimeoutSeconds, 120, 10, 900);

    public int ScalePercent => Clamp(Scale, 100, 50, 400);

    public int MarginPixels => Clamp(Margin, 10, 0, 200);

    public string OutputPath => string.IsNullOrWhiteSpace(OutputDirectory)
        ? Path.Combine(Path.GetTempPath(), "devtools-mcp", "archi")
        : Path.GetFullPath(ArchiPaths.ExpandHome(OutputDirectory.Trim()));

    public bool TryValidate(out string? executable, out string? reason)
    {
        executable = null;
        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            reason = "Archi:ExecutablePath not set";
            return false;
        }

        executable = ArchiPaths.ResolveExecutable(ExecutablePath);
        if (executable is null)
        {
            reason = $"Archi not found at {ExecutablePath.Trim()}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Renderer) && !UseJArchi && !string.Equals(Renderer.Trim(), HtmlReport, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Archi:Renderer must be HtmlReport or jArchi";
            return false;
        }

        reason = null;
        return true;
    }

    private static int Clamp(string? value, int fallback, int min, int max) =>
        int.TryParse(value, out var v) ? Math.Clamp(v, min, max) : fallback;
}

internal static class ArchiPaths
{
    public static string ExpandHome(string path) =>
        path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;

    /// <summary>
    /// Accepts the executable itself, its folder, or (macOS) the Archi.app bundle. Returns null when nothing runnable
    /// is there.
    /// </summary>
    public static string? ResolveExecutable(string configured)
    {
        var path = Path.GetFullPath(ExpandHome(configured.Trim().Trim('"')));
        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        string[] candidates = [Path.Combine(path, "Contents", "MacOS", "Archi"), Path.Combine(path, "Archi.exe"), Path.Combine(path, "Archi")];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The server runs with the user's file permissions, so tools only write .png/.xml files and never into hidden or
    /// application-data folders (~/.ssh, ~/.config, AppData, ...), where a prompt-injected path could do damage.
    /// The configured output folder is always allowed.
    /// </summary>
    public static string ValidateOutput(string path, string trustedRoot, params string[] extensions)
    {
        var full = Path.GetFullPath(ExpandHome(path.Trim()));
        if (extensions.Length > 0 && !extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{full}: expected a {string.Join(" or ", extensions)} file.");
        }

        // The configured output folder may itself live in AppData (Windows %TEMP%). Links are followed, so a folder
        // that points into ~/.ssh is refused like ~/.ssh itself.
        Core.Security.PathGuard.EnsureOrdinary(full, [trustedRoot], () =>
            new ArgumentException($"Refusing to write {full}: hidden and application-data folders are off limits."));

        return full;
    }

    /// <summary>A view name as a file name: invalid characters replaced, at most 80 characters.</summary>
    public static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\\' or '/' ? '_' : c).ToArray();
        var result = new string(chars).Trim('.', ' ');
        return result.Length == 0 ? "view" : result.Length > 80 ? result[..80] : result;
    }
}
