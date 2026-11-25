using System.Text.RegularExpressions;

namespace DevToolsMcp.Server.Modules.Jira;

public static partial class JiraJql
{
    /// <summary>
    /// Builds the JQL for <c>jira_search</c>: JQL passes through; a bare issue key becomes <c>key = KEY</c>;
    /// other plain text becomes <c>text ~ "..." ORDER BY updated DESC</c>.
    /// </summary>
    public static string Build(string query)
    {
        var q = query.Trim();
        if (IssueKey().IsMatch(q))
        {
            return $"key = {q.ToUpperInvariant()}";
        }

        return LooksLikeJql(q) ? q : $"text ~ \"{q.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" ORDER BY updated DESC";
    }

    public static bool LooksLikeJql(string query) => JqlOperator().IsMatch(query);

    // field op value (=, !=, ~, !~, <, >, <=, >=), field [not] in (...), field is [not] empty/null, or a leading ORDER BY
    [GeneratedRegex(@"\b[A-Za-z][\w.]*\s*(?:!=|!~|>=|<=|=|~|>|<)|\b[A-Za-z][\w.]*\s+(?:not\s+)?in\s*\(|\b[A-Za-z][\w.]*\s+is\s+(?:not\s+)?(?:empty|null)\b|^\s*order\s+by\b", RegexOptions.IgnoreCase)]
    private static partial Regex JqlOperator();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*-\d+$")]
    private static partial Regex IssueKey();
}
