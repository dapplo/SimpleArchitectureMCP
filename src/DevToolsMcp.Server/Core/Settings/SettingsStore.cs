using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;

namespace DevToolsMcp.Server.Core.Settings;

/// <summary>
/// Per-user settings file written by the settings page (default: <c>%APPDATA%/devtools-mcp/settings.json</c> or
/// <c>~/.config/devtools-mcp/settings.json</c>). It is loaded after appsettings.json and before environment
/// variables and the command line, so Aspire/env values still win.
/// </summary>
public sealed class SettingsStore(string filePath, IConfigurationRoot configuration)
{
    private static readonly Lock Gate = new();

    public string FilePath { get; } = filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "devtools-mcp", "settings.json");

    /// <summary>Adds the settings file as a configuration source just before the environment variable sources.</summary>
    public static string AddTo(ConfigurationManager configuration)
    {
        var path = Path.GetFullPath(configuration["DevToolsMcp:SettingsFile"] is { Length: > 0 } p ? p : DefaultPath());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var source = new JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = false };
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
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                root.WriteTo(writer);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); // contains tokens
            }

            File.Move(temp, FilePath, overwrite: true);
        }

        configuration.Reload();
    }
}
