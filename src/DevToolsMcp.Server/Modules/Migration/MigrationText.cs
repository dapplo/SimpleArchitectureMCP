using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DevToolsMcp.Server.Modules.Migration;

/// <summary>Text rewriting between the two systems: dates, mentions, attachment references, labels and limits.</summary>
internal static partial class MigrationText
{
    /// <summary>GitHub rejects issue and comment bodies over 65,536 characters.</summary>
    public const int GitHubMaxBody = 65_000;

    /// <summary>Jira Cloud rejects descriptions and comments over 32,767 characters (wiki markup is a little longer than Markdown).</summary>
    public const int JiraMaxMarkdown = 30_000;

    /// <summary>Parses Jira's <c>2026-11-01T10:00:00.000+0000</c> (offset without a colon).</summary>
    public static DateTimeOffset? JiraDate(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return null;
        }

        var s = JiraOffset().Replace(iso.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    public static string Stamp(DateTimeOffset? date) =>
        date?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "unknown date";

    public static string Day(DateTimeOffset? date) =>
        date?.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown date";

    public static string Truncate(string text, int max, string url) =>
        text.Length <= max ? text : text[..max].TrimEnd() + $"\n\n*(truncated; full text: {url})*";

    /// <summary>
    /// Jira → GitHub Markdown fix-ups: attachment images and <c>[^file]</c> references point at the copied or linked
    /// files, and <c>@user</c> becomes the mapped GitHub login, or inert <c>`@user`</c> so nobody unrelated is pinged.
    /// Fenced code blocks are left alone.
    /// </summary>
    public static string ForGitHub(string markdown, IReadOnlyDictionary<string, string> attachmentUrls, Func<string, string?> login)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        var sb = new StringBuilder(markdown.Length + 64);
        var inFence = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                sb.Append(line).Append('\n');
                continue;
            }

            if (inFence)
            {
                sb.Append(line).Append('\n');
                continue;
            }

            var l = ImageRef().Replace(line, m => attachmentUrls.TryGetValue(m.Groups[2].Value, out var url)
                ? $"![{(m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value)}]({url})"
                : m.Value);
            l = AttachmentRef().Replace(l, m => attachmentUrls.TryGetValue(m.Groups[1].Value, out var url) ? $"[{m.Groups[1].Value}]({url})" : m.Value);
            l = Mention().Replace(l, m => login(m.Groups[1].Value) is { } gh ? "@" + gh : "`@" + m.Groups[1].Value + "`");
            sb.Append(l).Append('\n');
        }

        return sb.ToString(0, sb.Length - 1);
    }

    /// <summary>Attachment URLs in GitHub Markdown (user-attachments, user-images and legacy repository assets).</summary>
    public static IEnumerable<string> GitHubAssets(string? markdown, string webHost)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            yield break;
        }

        foreach (Match m in AssetUrl().Matches(markdown))
        {
            var url = m.Value.TrimEnd('.', ',');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var host = uri.Host;
            var isAsset = (string.Equals(host, webHost, StringComparison.OrdinalIgnoreCase)
                           && (uri.AbsolutePath.StartsWith("/user-attachments/", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/assets/", StringComparison.Ordinal)))
                          || host.EndsWith("user-images.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            if (isAsset)
            {
                yield return url;
            }
        }
    }

    /// <summary>Points Markdown images at Jira attachments by file name (<c>![x](name)</c> becomes <c>!name!</c>).</summary>
    public static string ImagesToAttachments(string markdown, IReadOnlyDictionary<string, string> fileNames) =>
        fileNames.Count == 0 || string.IsNullOrEmpty(markdown)
            ? markdown
            : MarkdownImage().Replace(markdown, m => fileNames.TryGetValue(m.Groups[2].Value, out var name) ? $"![{m.Groups[1].Value}]({name})" : m.Value);

    /// <summary>A file name for a downloaded GitHub attachment (user-attachments asset URLs have no extension).</summary>
    public static string AssetFileName(string url, string? headerName, string? mediaType, ISet<string> used)
    {
        var name = !string.IsNullOrWhiteSpace(headerName) ? headerName.Trim() : Uri.UnescapeDataString(new Uri(url).Segments[^1].TrimEnd('/'));
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '!' or '|' or '[' or ']' ? '_' : c));
        if (Path.GetExtension(name).Length == 0 && Extension(mediaType) is { } ext)
        {
            name += ext;
        }

        if (name.Length == 0)
        {
            name = "attachment";
        }

        var unique = name;
        for (var i = 2; !used.Add(unique); i++)
        {
            unique = $"{Path.GetFileNameWithoutExtension(name)}-{i}{Path.GetExtension(name)}";
        }

        return unique;
    }

    private static string? Extension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        "text/plain" => ".txt",
        "application/json" => ".json",
        _ => null,
    };

    /// <summary>Jira labels cannot contain spaces.</summary>
    public static string JiraLabel(string label) => Whitespace().Replace(label.Trim(), "-");

    /// <summary>GitHub label names are limited to 50 characters.</summary>
    public static string GitHubLabel(string label) => label.Trim() is { Length: > 50 } l ? l[..50] : label.Trim();

    /// <summary>Maps a Jira resolution to a GitHub close reason.</summary>
    public static string StateReason(string? resolution)
    {
        var r = resolution?.Trim().ToLowerInvariant() ?? string.Empty;
        if (r.Contains("duplicate", StringComparison.Ordinal))
        {
            return "duplicate";
        }

        return r.Contains("won't", StringComparison.Ordinal) || r.Contains("wont", StringComparison.Ordinal)
               || r.Contains("not a", StringComparison.Ordinal) || r.Contains("cannot", StringComparison.Ordinal)
               || r.Contains("incomplete", StringComparison.Ordinal) || r.Contains("reject", StringComparison.Ordinal)
               || r.Contains("declin", StringComparison.Ordinal) || r.Contains("obsolete", StringComparison.Ordinal)
               || r.Contains("invalid", StringComparison.Ordinal)
            ? "not_planned"
            : "completed";
    }

    /// <summary>Resolution names to try when a Jira workflow asks for one, per GitHub close reason.</summary>
    public static string[] Resolutions(string? stateReason) => stateReason switch
    {
        "not_planned" => ["Won't Do", "Won't Fix", "Declined"],
        "duplicate" => ["Duplicate"],
        _ => ["Done", "Fixed"],
    };

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex JiraOffset();

    // ![alt](file.png) as written by JiraWikiConverter for !file.png!
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s/]+)\)")]
    private static partial Regex ImageRef();

    // `file` (attachment) as written by JiraWikiConverter for [^file]
    [GeneratedRegex(@"`([^`]+)` \(attachment\)")]
    private static partial Regex AttachmentRef();

    [GeneratedRegex(@"(?<![\w`@/])@([A-Za-z0-9][\w.:\-]*[\w])")]
    private static partial Regex Mention();

    [GeneratedRegex(@"https://[^\s)\]""'<>]+")]
    private static partial Regex AssetUrl();

    [GeneratedRegex(@"!\[([^\]]*)\]\((https://[^)\s]+)\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
