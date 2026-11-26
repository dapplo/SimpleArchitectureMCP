using System.Globalization;
using System.Text.RegularExpressions;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Modules.GitHub;

/// <summary>Settings-page download: an issue or pull request with its comments as Markdown, as <c>github_get_issue</c> returns it.</summary>
public sealed partial class GitHubService
{
    public async Task<ConversionFile> DownloadIssueAsync(string reference, CancellationToken ct)
    {
        var (repo, number) = ParseIssueReference(reference);
        var markdown = await GetIssueAsync(repo, number, MaxComments, ct).ConfigureAwait(false);
        var name = (repo ?? Api.DefaultRepo ?? "issue").Replace('/', '-') + "-" + number.ToString(CultureInfo.InvariantCulture);
        return ConversionFile.FromMarkdown(name, markdown);
    }

    /// <summary>owner/repo#123, #123 or 123 (default repository), or a pasted …/owner/repo/issues/123 or /pull/123 URL.</summary>
    internal static (string? Repo, int Number) ParseIssueReference(string? reference)
    {
        var text = reference?.Trim() ?? string.Empty;
        var m = IssueUrl().Match(text);
        if (!m.Success)
        {
            m = IssueShorthand().Match(text);
        }

        if (!m.Success || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"'{reference}' is not owner/repo#123, #123 or an issue URL.");
        }

        return (m.Groups[1].Success && m.Groups[1].Length > 0 ? ValidateRepo(m.Groups[1].Value) : null, number);
    }

    [GeneratedRegex(@"^https?://[^/]+/([^/]+/[^/#?]+)/(?:issues|pull)/(\d{1,9})(?!\d)")]
    private static partial Regex IssueUrl();

    [GeneratedRegex(@"^([^\s#/]+/[^\s#/]+)?#?(\d{1,9})$")]
    private static partial Regex IssueShorthand();
}
