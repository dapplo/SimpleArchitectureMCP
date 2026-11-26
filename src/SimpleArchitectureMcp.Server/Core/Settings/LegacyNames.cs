using Microsoft.Extensions.Configuration.Memory;

namespace SimpleArchitectureMcp.Server.Core.Settings;

/// <summary>
/// Keeps setups from before the rename (devtools-mcp, configuration section <c>DevToolsMcp</c>) working:
/// old <c>DevToolsMcp:*</c> keys still apply where the new key is unset, and the old settings folder is
/// copied once to the new one. The old folder is left as it was.
/// </summary>
public static class LegacyNames
{
    public const string Section = "DevToolsMcp";
    public const string Folder = "devtools-mcp";
    public const string NewSection = "SimpleArchitectureMcp";

    /// <summary>
    /// Maps every <c>DevToolsMcp:X</c> (env <c>DevToolsMcp__X</c>, <c>--DevToolsMcp:X</c>, an old settings file)
    /// to <c>SimpleArchitectureMcp:X</c> when no source sets the new key. Added as the lowest-priority source.
    /// </summary>
    public static void AddFallback(IConfigurationBuilder builder, IConfiguration configuration)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in configuration.GetSection(Section).AsEnumerable(makePathsRelative: true))
        {
            var newKey = $"{NewSection}:{key}";
            if (value is not null && configuration[newKey] is null)
            {
                values[newKey] = value;
            }
        }

        if (values.Count > 0)
        {
            builder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = values });
        }
    }

    /// <summary>
    /// First start after the rename: copies the files of the old settings folder (sibling <c>devtools-mcp</c>)
    /// into <paramref name="newFolder"/>, if that doesn't exist yet. Links are skipped. Returns the old folder when it copied.
    /// </summary>
    public static string? MigrateFolder(string newFolder)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(newFolder));
        if (parent is null || Directory.Exists(newFolder))
        {
            return null;
        }

        var old = Path.Combine(parent, Folder);
        try
        {
            if (!Directory.Exists(old) || new DirectoryInfo(old).LinkTarget is not null)
            {
                return null;
            }

            Directory.CreateDirectory(newFolder);
            foreach (var file in new DirectoryInfo(old).EnumerateFiles())
            {
                if (file.LinkTarget is null)
                {
                    file.CopyTo(Path.Combine(newFolder, file.Name), overwrite: false); // keeps the 0600 mode on Unix
                }
            }

            return old;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not copy the old settings from {old}: {ex.Message}");
            return null;
        }
    }
}
