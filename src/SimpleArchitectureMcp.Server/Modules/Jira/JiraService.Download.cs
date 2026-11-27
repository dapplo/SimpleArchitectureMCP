using System.Text.RegularExpressions;
using SimpleArchitectureMcp.Server.Core.Modules;

namespace SimpleArchitectureMcp.Server.Modules.Jira;

/// <summary>Settings-page download: an issue with its comments as Markdown, as <c>jira_get_issue</c> returns it.</summary>
public sealed partial class JiraService
{
    public async Task<ConversionFile> DownloadIssueAsync(string reference, CancellationToken ct)
    {
        var key = ParseIssueReference(reference);
        return ConversionFile.FromMarkdown(key, await GetIssueAsync(key, MaxComments, ct).ConfigureAwait(false));
    }

    /// <summary>An issue key, or a pasted URL containing one (…/browse/ABC-123, …?selectedIssue=ABC-123).</summary>
    internal static string ParseIssueReference(string? reference)
    {
        var text = reference?.Trim() ?? string.Empty;
        if (!text.Contains('/', StringComparison.Ordinal))
        {
            return ValidateKey(text);
        }

        var matches = IssueKeyInUrl().Matches(text);
        return matches.Count > 0
            ? matches[^1].Groups[1].Value.ToUpperInvariant()
            : throw new ArgumentException($"No issue key (e.g. ABC-123) in '{reference}'.");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])([A-Za-z][A-Za-z0-9_]*-\d{1,10})(?!\d)")]
    private static partial Regex IssueKeyInUrl();
}
