using System.Text.RegularExpressions;

namespace DevToolsMcp.Server.Modules.Confluence;

public static partial class ConfluenceCql
{
    /// <summary>
    /// Builds the CQL for <c>confluence_search</c>: CQL passes through; plain text becomes
    /// <c>type = page AND text ~ "..."</c>. A space key is AND-ed in front (ORDER BY is kept last).
    /// </summary>
    public static string Build(string query, string? spaceKey)
    {
        var q = query.Trim();
        var cql = LooksLikeCql(q) ? q : $"type = page AND text ~ \"{EscapeString(q)}\"";

        if (string.IsNullOrWhiteSpace(spaceKey))
        {
            return cql;
        }

        if (!SpaceKey().IsMatch(spaceKey))
        {
            throw new ArgumentException($"Invalid space key '{spaceKey}'.");
        }

        var order = string.Empty;
        var m = OrderBy().Match(cql);
        if (m.Success)
        {
            order = " " + cql[m.Index..].Trim();
            cql = cql[..m.Index].Trim();
        }

        return $"space = \"{spaceKey}\" AND ({cql}){order}";
    }

    public static bool LooksLikeCql(string query) => CqlOperator().IsMatch(query);

    private static string EscapeString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // field op value (=, !=, ~, !~, <, >, <=, >=) or field [not] in (...)
    [GeneratedRegex(@"\b[A-Za-z][\w.]*\s*(?:!=|!~|>=|<=|=|~|>|<)|\b[A-Za-z][\w.]*\s+(?:not\s+)?in\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CqlOperator();

    [GeneratedRegex(@"\border\s+by\b", RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
    private static partial Regex OrderBy();

    [GeneratedRegex(@"^~?[A-Za-z0-9_\-]+$")]
    private static partial Regex SpaceKey();
}
