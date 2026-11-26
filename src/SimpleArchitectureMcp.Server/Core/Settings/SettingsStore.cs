using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;

namespace SimpleArchitectureMcp.Server.Core.Settings;

/// <summary>
/// Per-user settings file written by the settings page (default: <c>%APPDATA%/simple-architecture-mcp/settings.json</c> or
/// <c>~/.config/simple-architecture-mcp/settings.json</c>). It is loaded after appsettings.json and before environment
/// variables and the command line, so Aspire/env values still win.
/// </summary>
public sealed class SettingsStore(string filePath, IConfigurationRoot configuration)
{
    private static readonly Lock Gate = new();

    public string FilePath { get; } = filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "simple-architecture-mcp", "settings.json");

    /// <summary>Adds the settings file as a configuration source just before the environment variable sources.</summary>
    /// <param name="watch">Reload when another process (e.g. a second simple-architecture-mcp instance) changes the file.</param>
    public static string AddTo(ConfigurationManager configuration, bool watch = false)
    {
        LegacyNames.AddFallback(configuration, configuration);   // DevToolsMcp:* from before the rename
        var custom = configuration["SimpleArchitectureMcp:SettingsFile"] is { Length: > 0 } p ? p : null;
        var path = Path.GetFullPath(custom ?? DefaultPath());
        if (custom is null && LegacyNames.MigrateFolder(Path.GetDirectoryName(path)!) is { } old)
        {
            Console.Error.WriteLine($"Copied your settings from {old} to {Path.GetDirectoryName(path)}.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var source = new JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = watch };
        source.ResolveFileProvider();

        // Right after the last JSON file source (appsettings[.Environment].json), i.e. before the
        // non-prefixed environment variables and the command line.
        var sources = configuration.Sources;
        var index = sources.Count;
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                index = i + 1;
                break;
            }
        }

        sources.Insert(index, source);
        LegacyNames.AddFallback(configuration, configuration);   // a DevToolsMcp section in an old settings file
        return path;
    }

    /// <summary>Writes the given keys of one section (null = leave unchanged) and reloads configuration.</summary>
    public void Save(string section, IReadOnlyDictionary<string, string?> values)
    {
        lock (Gate)
        {
            var root = File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject existing ? existing : new JsonObject();
            if (root[section] is not JsonObject target)
            {
                target = new JsonObject();
                root[section] = target;
            }

            foreach (var (key, value) in values)
            {
                if (value is not null)
                {
                    target[key] = value;
                }
            }

            var temp = FilePath + ".tmp";
            File.Delete(temp); // a leftover from a crash may have other permissions; the mode below applies to new files only
            using (var stream = new FileStream(temp, PrivateFile()))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                root.WriteTo(writer);
            }

            File.Move(temp, FilePath, overwrite: true);
        }

        configuration.Reload();
    }

    /// <summary>
    /// The file holds tokens: on Linux and macOS it is created readable by the owner only (0600) from the start, so
    /// there is no moment in which it carries the umask's group/other read bits.
    /// </summary>
    internal static FileStreamOptions PrivateFile()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }
}
