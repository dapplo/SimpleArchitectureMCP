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

    /// <summary>
    /// Set just before the target issue is created, cleared once it is recorded. Still set on a later run means the
    /// earlier one stopped in between, so the target is searched for the issue before creating another.
    /// </summary>
    public DateTimeOffset? Creating { get; set; }

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
            return new MigrationMap(path, Read(path));
        }
    }

    private static MigrationMapFile Read(string path)
    {
        if (!File.Exists(path))
        {
            return new MigrationMapFile();
        }

        try
        {
            var data = JsonSerializer.Deserialize(File.ReadAllBytes(path), MigrationJsonContext.Default.MigrationMapFile) ?? new MigrationMapFile();
            data.Issues = new Dictionary<string, MigrationRecord>(data.Issues, StringComparer.OrdinalIgnoreCase);
            return data;
        }
        catch (JsonException ex)
        {
            throw new IOException($"Migration map {path} is not valid JSON ({ex.Message}). Fix or move it; nothing was migrated.", ex);
        }
    }

    public static string Key(string source, string target) => $"{source} -> {target}";

    public MigrationRecord? Get(string key)
    {
        lock (Gate)
        {
            return _data.Issues.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Records one issue. The file is re-read first and only this key is replaced, so runs that share the file (two
    /// calls at once, or the stdio and the HTTP instance) don't overwrite each other's records.
    /// </summary>
    public void Set(string key, MigrationRecord record)
    {
        lock (Gate)
        {
            foreach (var (k, r) in Read(Path).Issues)
            {
                _data.Issues[k] = r;
            }

            _data.Issues[key] = record;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = $"{Path}.{Guid.NewGuid():N}.tmp"; // unique: another process may be saving the same map
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_data, MigrationJsonContext.Default.MigrationMapFile));
            File.Move(temp, Path, overwrite: true);
        }
    }
}
