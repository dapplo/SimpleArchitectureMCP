using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>
/// Storage-format markup of the draw.io app's <c>drawio</c> macro (Cloud and Data Center). The macro shows the
/// diagram stored as the page attachment named <c>diagramName</c>; <c>revision</c> is that attachment's version.
/// </summary>
public static partial class DrawioMacro
{
    public static string Create(string diagramName, string pageId, int revision)
    {
        var name = SecurityElement.Escape(diagramName);
        return $"""<ac:structured-macro ac:name="drawio" ac:schema-version="1" ac:macro-id="{Guid.NewGuid()}">"""
            + Param("diagramName", name)
            + Param("diagramDisplayName", name)
            + Param("revision", revision.ToString(CultureInfo.InvariantCulture))
            + Param("pageId", pageId)
            + Param("simpleViewer", "false")
            + Param("lbox", "true")
            + Param("links", "auto")
            + Param("tbstyle", "top")
            + "</ac:structured-macro>";
    }

    /// <summary>
    /// Points the page's existing macro for <paramref name="diagramName"/> at <paramref name="revision"/>.
    /// Returns null when the page has no draw.io macro for that diagram.
    /// </summary>
    public static string? SetRevision(string storage, string diagramName, int revision)
    {
        var nameParam = Param("diagramName", SecurityElement.Escape(diagramName));
        var found = false;
        var result = Macro().Replace(storage, m =>
        {
            if (found || !m.Value.Contains(nameParam, StringComparison.Ordinal))
            {
                return m.Value;
            }

            found = true;
            var value = revision.ToString(CultureInfo.InvariantCulture);
            return RevisionParam().IsMatch(m.Value)
                ? RevisionParam().Replace(m.Value, Param("revision", value), 1)
                : m.Value.Replace(nameParam, nameParam + Param("revision", value), StringComparison.Ordinal);
        });
        return found ? result : null;
    }

    private static string Param(string name, string escapedValue) => $"""<ac:parameter ac:name="{name}">{escapedValue}</ac:parameter>""";

    [GeneratedRegex("""<ac:structured-macro\b[^>]*\bac:name="drawio"[^>]*>.*?</ac:structured-macro>""", RegexOptions.Singleline)]
    private static partial Regex Macro();

    [GeneratedRegex("""<ac:parameter ac:name="revision">[^<]*</ac:parameter>""")]
    private static partial Regex RevisionParam();
}
