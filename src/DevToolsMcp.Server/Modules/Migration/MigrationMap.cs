using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevToolsMcp.Server.Modules.Migration;

/// <summary>Progress of one source issue in one target. Saved after every step, so a re-run resumes where it stopped.</summary>
public sealed class MigrationRecord
{
    /// <summary><c>owner/repo#12</c> or <c>ABC-9</c>.</summary>
    public string? Target { get; set; }

    public string? Url { get; set; }

    /// <summary>GitHub database id of the created issue (sub-issue links need it).</summary>
    public long TargetId { get; set; }

    /// <summary>How many source comments have been copied.</summary>
    public int Comments { get; set; }

    public bool Attachments { get; set; }
    public bool Closed { get; set; }
    public bool Done { get; set; }
}

public sealed class MigrationMapFile
{
    /// <summary>Key: "source -> target container", e.g. <c>jira:acme.atlassian.net/ABC-1 -> github:github.com/o/r</c>.</summary>
    public Dictionary<string, MigrationRecord> Issues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(MigrationMapFile))]
public sealed partial class MigrationJsonContext : JsonSerializerContext;

/// <summary>
/// The local source → target record that makes migration idempotent: an issue is created once, and comments,
/// attachments and the status are copied once, however often a batch is re-run. Written atomically.
/// </summary>
public sealed class MigrationMap
{
    private static readonly Lock Gate = new();
    private readonly MigrationMapFile _data;

    public string Path { get; }

    private MigrationMap(string path, MigrationMapFile data)
    {
        Path = path;
        _data = data;
    }

    public static MigrationMap Load(string path)
    {
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                return new MigrationMap(path, new MigrationMapFile());
            }

            try
            {
                var data = JsonSerializer.Deserialize(File.ReadAllBytes(path), MigrationJsonContext.Default.MigrationMapFile) ?? new MigrationMapFile();
                data.Issues = new Dictionary<string, MigrationRecord>(data.Issues, StringComparer.OrdinalIgnoreCase);
                return new MigrationMap(path, data);
            }
            catch (JsonException ex)
            {
                throw new IOException($"Migration map {path} is not valid JSON ({ex.Message}). Fix or move it; nothing was migrated.", ex);
            }
        }
    }

    public static string Key(string source, string target) => $"{source} -> {target}";

    public MigrationRecord? Get(string key) => _data.Issues.GetValueOrDefault(key);

    public void Set(string key, MigrationRecord record)
    {
        lock (Gate)
        {
            _data.Issues[key] = record;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_data, MigrationJsonContext.Default.MigrationMapFile));
            File.Move(temp, Path, overwrite: true);
        }
    }
}
