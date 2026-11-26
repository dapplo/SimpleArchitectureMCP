using System.Net;
using System.Text.RegularExpressions;

namespace DevToolsMcp.Server.Core.Markdown;

/// <summary>
/// HTML entity references in Markdown text (<c>&amp;auml;</c>, <c>&amp;nbsp;</c>, <c>&amp;#8364;</c>, <c>&amp;#x1F600;</c>).
/// CommonMark decodes them to the character, so a model that writes <c>Gr&amp;ouml;&amp;szlig;e</c> gets "Größe" on the
/// page instead of the literal entity text.
/// </summary>
public static partial class HtmlEntities
{
    /// <summary>Decodes the entity reference at <paramref name="i"/>; false (nothing consumed) if there is none.</summary>
    public static bool TryDecode(string s, int i, out string decoded, out int length)
    {
        decoded = string.Empty;
        length = 0;
        if (s[i] != '&')
        {
            return false;
        }

        var m = Reference().Match(s, i);
        if (!m.Success || m.Index != i)
        {
            return false;
        }

        var value = WebUtility.HtmlDecode(m.Value);
        if (value == m.Value || value.Length == 0 || value.Any(c => char.IsControl(c) && c is not '\t' and not '\n'))
        {
            return false; // unknown name, or a NUL/control character: keep the text as written
        }

        decoded = value;
        length = m.Length;
        return true;
    }

    /// <summary>True if the text at <paramref name="i"/> would be decoded as an entity reference.</summary>
    public static bool StartsReference(string s, int i) => TryDecode(s, i, out _, out _);

    [GeneratedRegex("&(?:#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex Reference();
}
