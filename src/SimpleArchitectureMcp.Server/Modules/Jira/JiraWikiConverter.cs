using System.Text;
using System.Text.RegularExpressions;
using SimpleArchitectureMcp.Server.Core.Markdown;

namespace SimpleArchitectureMcp.Server.Modules.Jira;

/// <summary>
/// Converts between Markdown (what the model reads and writes) and Jira wiki markup (what REST API v2 stores for
/// descriptions and comments on both Cloud and Data Center). Markdown → wiki uses the shared
/// <see cref="MarkdownParser"/>; wiki → Markdown is a small line-based converter. No reflection, no libraries.
/// </summary>
public static partial class JiraWikiConverter
{
    // ---- Markdown → wiki ------------------------------------------------------------------------------------

    public static string ToWiki(string markdown)
    {
        var sb = new StringBuilder();
        WriteBlocks(sb, MarkdownParser.Parse(markdown ?? string.Empty));
        return sb.ToString().Trim('\n');
    }

    private static void WriteBlocks(StringBuilder sb, IEnumerable<MdBlock> blocks)
    {
        foreach (var block in blocks)
        {
            WriteBlock(sb, block);
        }
    }

    private static void WriteBlock(StringBuilder sb, MdBlock block)
    {
        switch (block)
        {
            case MdHeading h:
                sb.Append('h').Append(Math.Clamp(h.Level, 1, 6)).Append(". ");
                WriteInlines(sb, h.Content, inline: true);
                sb.Append("\n\n");
                break;

            case MdParagraph p:
                var start = sb.Length;
                WriteInlines(sb, p.Content, inline: false);
                if (sb.Length > start && sb[start] == '#')
                {
                    sb.Insert(start, '\\'); // would start a numbered list
                }

                sb.Append("\n\n");
                break;

            case MdCodeBlock code:
                sb.Append(string.IsNullOrEmpty(code.Language) ? "{noformat}" : "{code:" + code.Language + "}")
                    .Append('\n').Append(code.Code.TrimEnd('\n')).Append('\n')
                    .Append(string.IsNullOrEmpty(code.Language) ? "{noformat}" : "{code}").Append("\n\n");
                break;

            case MdList list:
                WriteList(sb, list, string.Empty);
                sb.Append('\n');
                break;

            case MdBlockQuote quote:
                var inner = new StringBuilder();
                WriteBlocks(inner, quote.Blocks);
                sb.Append("{quote}\n").Append(inner.ToString().Trim('\n')).Append("\n{quote}\n\n");
                break;

            case MdTable table:
                sb.Append("||");
                foreach (var cell in table.Header)
                {
                    WriteInlines(sb, cell, inline: true);
                    sb.Append("||");
                }

                sb.Append('\n');
                foreach (var row in table.Rows)
                {
                    sb.Append('|');
                    foreach (var cell in row)
                    {
                        WriteInlines(sb, cell, inline: true);
                        sb.Append('|');
                    }

                    sb.Append('\n');
                }

                sb.Append('\n');
                break;

            case MdThematicBreak:
                sb.Append("----\n\n");
                break;
        }
    }

    private static void WriteList(StringBuilder sb, MdList list, string parentMarker)
    {
        var marker = parentMarker + (list.Ordered ? '#' : '*');
        foreach (var item in list.Items)
        {
            sb.Append(marker).Append(' ');
            if (item.Checked is { } done)
            {
                sb.Append(done ? "\\[x\\] " : "\\[ \\] ");
            }

            var first = true;
            var trailing = new List<MdBlock>();
            foreach (var block in item.Blocks)
            {
                switch (block)
                {
                    case MdParagraph p:
                        if (!first)
                        {
                            sb.Append(" \\\\ ");
                        }

                        WriteInlines(sb, p.Content, inline: true);
                        first = false;
                        break;
                    case MdList nested:
                        sb.Append('\n');
                        WriteList(sb, nested, marker);
                        sb.Length--; // nested list already ended its last line
                        break;
                    default:
                        trailing.Add(block); // code blocks, quotes, tables cannot live inside a wiki list item
                        break;
                }
            }

            sb.Append('\n');
            if (trailing.Count > 0)
            {
                WriteBlocks(sb, trailing);
            }
        }
    }

    /// <param name="inline">true where a newline would end the construct (headings, list items, table cells).</param>
    private static void WriteInlines(StringBuilder sb, IReadOnlyList<MdInline> nodes, bool inline)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case MdText t:
                    Escape(sb, t.Text);
                    break;
                case MdStrong s:
                    sb.Append('*');
                    WriteInlines(sb, s.Content, inline);
                    sb.Append('*');
                    break;
                case MdEmphasis e:
                    sb.Append('_');
                    WriteInlines(sb, e.Content, inline);
                    sb.Append('_');
                    break;
                case MdStrike s:
                    sb.Append('-');
                    WriteInlines(sb, s.Content, inline);
                    sb.Append('-');
                    break;
                case MdCode c:
                    sb.Append("{{");
                    Escape(sb, c.Code);
                    sb.Append("}}");
                    break;
                case MdLink l:
                    var text = new StringBuilder();
                    WriteInlines(text, l.Content, inline: true);
                    sb.Append('[');
                    if (text.Length > 0 && text.ToString() != l.Url)
                    {
                        sb.Append(text).Append('|');
                    }

                    sb.Append(l.Url).Append(']');
                    break;
                case MdImage i:
                    sb.Append('!').Append(i.Url).Append('!');
                    break;
                case MdLineBreak:
                    sb.Append(inline ? " \\\\ " : "\n");
                    break;
            }
        }
    }

    private static void Escape(StringBuilder sb, string text)
    {
        foreach (var c in text)
        {
            if (c is '*' or '_' or '{' or '}' or '[' or ']' or '|' or '!' or '^' or '~')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }
    }

    // ---- wiki → Markdown ------------------------------------------------------------------------------------

    public static string ToMarkdown(string? wiki)
    {
        if (string.IsNullOrWhiteSpace(wiki))
        {
            return string.Empty;
        }

        var output = new List<string>();
        var quoteDepth = 0;
        string? fenceEnd = null;     // "{code}" or "{noformat}" while inside a code block
        var tableHasHeader = false;
        var numbers = new List<int>(); // ordered-list counters per nesting depth

        // {quote}/{panel} may share a line with text ("{quote}text" ... "text{quote}"): give each tag its own line.
        var normalized = QuoteTag().Replace(wiki.Replace("\r\n", "\n").Replace('\r', '\n'), "\n$1\n");
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine;
            var prefix = quoteDepth > 0 ? "> " : string.Empty;

            if (fenceEnd is not null)
            {
                var end = line.IndexOf(fenceEnd, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    output.Add(prefix + line);
                    continue;
                }

                if (end > 0)
                {
                    output.Add(prefix + line[..end]);
                }

                output.Add(prefix + "```");
                line = line[(end + fenceEnd.Length)..];
                fenceEnd = null;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
            }

            var code = CodeStart().Match(line);
            if (code.Success)
            {
                var macro = code.Groups[1].Value.ToLowerInvariant();
                output.Add(prefix + "```" + (macro == "code" ? CodeLanguage(code.Groups[2].Value) : string.Empty));
                var rest = code.Groups[3].Value;
                var close = "{" + macro + "}";
                var closeAt = rest.IndexOf(close, StringComparison.OrdinalIgnoreCase);
                if (closeAt >= 0)
                {
                    if (closeAt > 0)
                    {
                        output.Add(prefix + rest[..closeAt]);
                    }

                    output.Add(prefix + "```");
                }
                else
                {
                    if (rest.Length > 0)
                    {
                        output.Add(prefix + rest);
                    }

                    fenceEnd = close;
                }

                continue;
            }

            var trimmed = line.Trim();
            if (QuoteOrPanel().IsMatch(trimmed))
            {
                quoteDepth = quoteDepth > 0 ? 0 : 1; // {quote}/{panel} open and close with the same tag
                continue;
            }

            if (trimmed.Length == 0)
            {
                tableHasHeader = false;
                numbers.Clear();
                output.Add(quoteDepth > 0 ? ">" : string.Empty);
                continue;
            }

            if (trimmed.StartsWith("||", StringComparison.Ordinal))
            {
                var cells = SplitCells(trimmed, "||");
                output.Add(prefix + "| " + string.Join(" | ", cells.Select(c => Inline(c, inTable: true))) + " |");
                output.Add(prefix + "|" + string.Concat(cells.Select(_ => " --- |")));
                tableHasHeader = true;
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                var cells = SplitCells(trimmed, "|");
                output.Add(prefix + "| " + string.Join(" | ", cells.Select(c => Inline(c, inTable: true))) + " |");
                if (!tableHasHeader)
                {
                    output.Add(prefix + "|" + string.Concat(cells.Select(_ => " --- |")));
                    tableHasHeader = true;
                }

                continue;
            }

            tableHasHeader = false;

            var heading = Heading().Match(trimmed);
            if (heading.Success)
            {
                output.Add(prefix + new string('#', heading.Groups[1].Value[0] - '0') + " " + Inline(heading.Groups[2].Value, false));
                continue;
            }

            var bq = BlockQuoteLine().Match(trimmed);
            if (bq.Success)
            {
                output.Add("> " + Inline(bq.Groups[1].Value, false));
                continue;
            }

            if (trimmed == "----")
            {
                output.Add(prefix + "---");
                continue;
            }

            var item = ListItem().Match(trimmed);
            if (item.Success)
            {
                var marker = item.Groups[1].Value;
                var indent = 0;
                foreach (var m in marker[..^1])
                {
                    indent += m == '#' ? 3 : 2;
                }

                var depth = marker.Length;
                if (numbers.Count > depth)
                {
                    numbers.RemoveRange(depth, numbers.Count - depth);
                }

                while (numbers.Count < depth)
                {
                    numbers.Add(0);
                }

                numbers[depth - 1] = marker[^1] == '#' ? numbers[depth - 1] + 1 : 0;
                var bullet = marker[^1] == '#' ? numbers[depth - 1].ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " : "- ";
                var text = TaskBox().Replace(item.Groups[2].Value, mm => mm.Groups[1].Value == " " ? "[ ] " : "[x] ");
                output.Add(prefix + new string(' ', indent) + bullet + Inline(text, false));
                continue;
            }

            numbers.Clear();
            output.Add(prefix + Inline(line, false));
        }

        if (fenceEnd is not null)
        {
            output.Add("```");
        }

        return string.Join('\n', output).Trim('\n');
    }

    private static string CodeLanguage(string parameters)
    {
        foreach (var p in parameters.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = p.IndexOf('=');
            if (eq < 0)
            {
                return p;
            }

            if (p[..eq].Trim().Equals("language", StringComparison.OrdinalIgnoreCase))
            {
                return p[(eq + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>Splits a table row on its separator, ignoring separators inside [links] and {{code}}.</summary>
    private static List<string> SplitCells(string row, string separator)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var bracket = 0;
        var mono = false;
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == '\\' && i + 1 < row.Length)
            {
                current.Append(row, i, 2);
                i++;
                continue;
            }

            if (string.CompareOrdinal(row, i, "{{", 0, 2) == 0 || string.CompareOrdinal(row, i, "}}", 0, 2) == 0)
            {
                mono = row[i] == '{';
                current.Append(row, i, 2);
                i++;
                continue;
            }

            bracket += row[i] == '[' ? 1 : row[i] == ']' && bracket > 0 ? -1 : 0;
            if (bracket == 0 && !mono && string.CompareOrdinal(row, i, separator, 0, separator.Length) == 0)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                i += separator.Length - 1;
                continue;
            }

            current.Append(row[i]);
        }

        cells.Add(current.ToString().Trim());
        if (cells.Count > 0 && cells[0].Length == 0)
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && cells[^1].Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    internal static string Inline(string text, bool inTable)
    {
        // Protect monospace spans, then convert links/images, then emphasis; escapes are resolved last.
        var spans = new List<string>();
        string Protect(string s)
        {
            spans.Add(s);
            return "\u0002" + (spans.Count - 1) + "\u0003";
        }

        text = Monospace().Replace(text, m => Protect("`" + Unescape(m.Groups[1].Value) + "`"));
        text = Color().Replace(text, string.Empty);
        text = Mention().Replace(text, m => "@" + m.Groups[1].Value);
        text = AttachmentLink().Replace(text, m => Protect("`" + m.Groups[1].Value + "` (attachment)"));
        text = Link().Replace(text, m =>
        {
            var label = m.Groups[1].Success ? m.Groups[1].Value : null;
            var url = m.Groups[2].Value.Trim();
            return Protect(label is null ? (url.Contains("://", StringComparison.Ordinal) ? "<" + url + ">" : "[" + url + "]") : "[" + Unescape(label) + "](" + url + ")");
        });
        text = Image().Replace(text, m => Protect("![](" + m.Groups[1].Value + ")"));
        text = Bold().Replace(text, "\u0001$1\u0001");
        text = Italic().Replace(text, "*$1*");
        text = Strike().Replace(text, "~~$1~~");
        text = text.Replace("\\\\", inTable ? "<br>" : "\n");
        text = Unescape(text).Replace("\u0001", "**");
        return Placeholder().Replace(text, m => spans[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private static string Unescape(string s) => EscapedChar().Replace(s, "$1");

    [GeneratedRegex(@"^\s*\{(code|noformat)(?::([^}]*))?\}(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeStart();

    [GeneratedRegex(@"^\{(?:quote|panel(?::[^}]*)?)\}$", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteOrPanel();

    [GeneratedRegex(@"\n?(\{(?:quote|panel(?::[^}]*)?)\})\n?", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteTag();

    [GeneratedRegex(@"^h([1-6])\.\s*(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^bq\.\s+(.*)$")]
    private static partial Regex BlockQuoteLine();

    [GeneratedRegex(@"^([*#]+|-)\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\\?\[([ xX])\\?\]\s")]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"\{\{(.+?)\}\}")]
    private static partial Regex Monospace();

    [GeneratedRegex(@"\{color(?::[^}]*)?\}", RegexOptions.IgnoreCase)]
    private static partial Regex Color();

    [GeneratedRegex(@"\[~(?:accountid:)?([^\]]+)\]")]
    private static partial Regex Mention();

    [GeneratedRegex(@"\[\^([^\]]+)\]")]
    private static partial Regex AttachmentLink();

    [GeneratedRegex(@"(?<!\\)\[(?:([^\[\]|]*)\|)?([^\[\]|]+)\]")]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<![\w\\])!((?:https?://)?[^!\s|]+\.[A-Za-z0-9]+)(?:\|[^!]*)?!")]
    private static partial Regex Image();

    [GeneratedRegex(@"(?<![\w*\\])\*(?![\s*])(.+?)(?<![\s\\])\*(?![\w*])")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\w\\])_(?![\s_])(.+?)(?<![\s\\])_(?!\w)")]
    private static partial Regex Italic();

    [GeneratedRegex(@"(?<![\w\\-])-(?![\s-])(.+?)(?<![\s\\-])-(?![\w-])")]
    private static partial Regex Strike();

    [GeneratedRegex(@"\\([*_{}\[\]|!^~+\-#?])")]
    private static partial Regex EscapedChar();

    [GeneratedRegex("\u0002(\\d+)\u0003")]
    private static partial Regex Placeholder();
}
