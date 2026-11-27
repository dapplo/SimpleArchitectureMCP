using System.Text;
using System.Text.RegularExpressions;

namespace SimpleArchitectureMcp.Server.Core.Markdown;

/// <summary>
/// Small, deterministic, reflection-free Markdown parser (CommonMark/GFM subset): ATX headings, paragraphs,
/// fenced code, block quotes, nested ordered/unordered/task lists, pipe tables, thematic breaks and the usual
/// inline constructs (strong, emphasis, strike, code, links, images, hard breaks, backslash escapes).
/// </summary>
public static partial class MarkdownParser
{
    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        return ParseBlocks(lines);
    }

    private static List<MdBlock> ParseBlocks(IReadOnlyList<string> lines)
    {
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                i++;
                continue;
            }

            var fence = FenceOpen().Match(line);
            if (fence.Success)
            {
                blocks.Add(ParseFence(lines, ref i, fence));
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                blocks.Add(new MdHeading(heading.Groups[1].Length, InlineParser.Parse(heading.Groups[2].Value.Trim())));
                i++;
                continue;
            }

            if (ThematicBreak().IsMatch(line))
            {
                blocks.Add(new MdThematicBreak());
                i++;
                continue;
            }

            if (BlockQuote().IsMatch(line))
            {
                var inner = new List<string>();
                while (i < lines.Count && BlockQuote().IsMatch(lines[i]))
                {
                    inner.Add(BlockQuote().Replace(lines[i], string.Empty, 1));
                    i++;
                }

                blocks.Add(new MdBlockQuote(ParseBlocks(inner)));
                continue;
            }

            if (IsTableStart(lines, i))
            {
                blocks.Add(ParseTable(lines, ref i));
                continue;
            }

            if (TryListMarker(line, out _))
            {
                blocks.Add(ParseList(lines, ref i));
                continue;
            }

            var para = new List<string> { line };
            i++;
            while (i < lines.Count && !IsBlank(lines[i]) && !StartsBlock(lines, i))
            {
                para.Add(lines[i]);
                i++;
            }

            blocks.Add(new MdParagraph(InlineParser.Parse(JoinParagraph(para))));
        }

        return blocks;
    }

    private static MdCodeBlock ParseFence(IReadOnlyList<string> lines, ref int i, Match open)
    {
        var indent = open.Groups[1].Length;
        var fence = open.Groups[2].Value;
        var info = open.Groups[3].Value.Trim();
        string? language = null;
        string? rest = null;
        if (info.Length > 0)
        {
            var space = info.IndexOf(' ');
            language = space < 0 ? info : info[..space];
            rest = space < 0 ? null : info[(space + 1)..].Trim();
        }

        var code = new StringBuilder();
        i++;
        while (i < lines.Count && !IsClosingFence(lines[i], fence))
        {
            var l = lines[i];
            var strip = 0;
            while (strip < indent && strip < l.Length && l[strip] == ' ')
            {
                strip++;
            }

            code.Append(l, strip, l.Length - strip).Append('\n');
            i++;
        }

        i++; // closing fence (or EOF)
        if (code.Length > 0)
        {
            code.Length--;
        }

        return new MdCodeBlock(language, string.IsNullOrEmpty(rest) ? null : rest, code.ToString());
    }

    private static bool IsClosingFence(string line, string fence)
    {
        var t = line.Trim();
        return t.Length >= fence.Length && t.All(c => c == fence[0]) && Indent(line) <= 3;
    }

    private static MdList ParseList(IReadOnlyList<string> lines, ref int i)
    {
        TryListMarker(lines[i], out var first);
        var items = new List<MdListItem>();

        while (i < lines.Count)
        {
            if (IsBlank(lines[i]))
            {
                var next = NextNonBlank(lines, i);
                if (next < lines.Count && TryListMarker(lines[next], out var sib) && sib.Ordered == first.Ordered && sib.Indent <= first.Indent + 1)
                {
                    i = next;
                    continue;
                }

                break;
            }

            if (!TryListMarker(lines[i], out var marker) || marker.Ordered != first.Ordered || marker.Indent > first.Indent + 1)
            {
                break;
            }

            var itemLines = new List<string> { marker.Content };
            i++;
            while (i < lines.Count)
            {
                var l = lines[i];
                if (IsBlank(l))
                {
                    var next = NextNonBlank(lines, i);
                    if (next < lines.Count && Indent(lines[next]) >= marker.ContentIndent)
                    {
                        for (; i < next; i++)
                        {
                            itemLines.Add(string.Empty);
                        }

                        continue;
                    }

                    break;
                }

                if (Indent(l) >= marker.ContentIndent)
                {
                    itemLines.Add(l[marker.ContentIndent..]);
                    i++;
                    continue;
                }

                if (TryListMarker(l, out _) || StartsBlock(lines, i) || IsBlank(itemLines[^1]))
                {
                    break;
                }

                itemLines.Add(l.TrimStart()); // lazy paragraph continuation
                i++;
            }

            bool? check = null;
            var head = itemLines[0];
            if (head.StartsWith("[ ] ", StringComparison.Ordinal) || head == "[ ]")
            {
                check = false;
            }
            else if (head.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) || head.Equals("[x]", StringComparison.OrdinalIgnoreCase))
            {
                check = true;
            }

            if (check is not null)
            {
                itemLines[0] = head.Length > 4 ? head[4..] : string.Empty;
            }

            items.Add(new MdListItem(ParseBlocks(itemLines), check));
        }

        return new MdList(first.Ordered, first.Start, items);
    }

    private static MdTable ParseTable(IReadOnlyList<string> lines, ref int i)
    {
        var header = SplitRow(lines[i]);
        i += 2; // header + delimiter row
        var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
        while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|'))
        {
            var cells = SplitRow(lines[i]);
            var row = new List<IReadOnlyList<MdInline>>(header.Count);
            for (var c = 0; c < header.Count; c++)
            {
                row.Add(InlineParser.Parse(c < cells.Count ? cells[c] : string.Empty));
            }

            rows.Add(row);
            i++;
        }

        return new MdTable(header.Select(h => (IReadOnlyList<MdInline>)InlineParser.Parse(h)).ToList(), rows);
    }

    /// <summary>Splits a pipe-table row on unescaped pipes.</summary>
    internal static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|'))
        {
            t = t[1..];
        }

        if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal))
        {
            t = t[..^1];
        }

        var cells = new List<string>();
        var sb = new StringBuilder();
        for (var k = 0; k < t.Length; k++)
        {
            if (t[k] == '\\' && k + 1 < t.Length)
            {
                // GFM: "\|" is a literal pipe (also inside code spans); other escapes stay for the inline parser.
                if (t[k + 1] != '|')
                {
                    sb.Append('\\');
                }

                sb.Append(t[k + 1]);
                k++;
            }
            else if (t[k] == '|')
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(t[k]);
            }
        }

        cells.Add(sb.ToString().Trim());
        return cells;
    }

    private static string JoinParagraph(List<string> lines)
    {
        var sb = new StringBuilder();
        for (var k = 0; k < lines.Count; k++)
        {
            var l = lines[k].TrimStart();
            var last = k == lines.Count - 1;
            if (last)
            {
                sb.Append(l.TrimEnd());
            }
            else if (l.EndsWith("  ", StringComparison.Ordinal))
            {
                sb.Append(l.TrimEnd()).Append('\n'); // '\n' = hard break for the inline parser
            }
            else if (l.EndsWith('\\') && !l.EndsWith("\\\\", StringComparison.Ordinal))
            {
                sb.Append(l, 0, l.Length - 1).Append('\n');
            }
            else
            {
                sb.Append(l.TrimEnd()).Append(' ');
            }
        }

        return sb.ToString();
    }

    private static bool StartsBlock(IReadOnlyList<string> lines, int i)
    {
        var l = lines[i];
        return FenceOpen().IsMatch(l) || Heading().IsMatch(l) || ThematicBreak().IsMatch(l) || BlockQuote().IsMatch(l)
            || TryListMarker(l, out _) || IsTableStart(lines, i);
    }

    private static bool IsTableStart(IReadOnlyList<string> lines, int i) =>
        i + 1 < lines.Count && lines[i].Contains('|') && lines[i + 1].Contains('|') && TableDelimiter().IsMatch(lines[i + 1]);

    internal readonly record struct ListMarker(bool Ordered, int Start, int Indent, int ContentIndent, string Content);

    internal static bool TryListMarker(string line, out ListMarker marker)
    {
        var m = ListItem().Match(line);
        if (!m.Success)
        {
            marker = default;
            return false;
        }

        var indent = m.Groups[1].Length;
        var bullet = m.Groups[2].Value;
        var spaces = m.Groups[3].Length;
        var content = m.Groups[4].Value;
        if (spaces is 0 or > 4)
        {
            spaces = 1;
            content = line[(indent + bullet.Length)..].TrimStart();
        }

        var ordered = char.IsAsciiDigit(bullet[0]);
        var start = ordered ? int.Parse(bullet[..^1], System.Globalization.CultureInfo.InvariantCulture) : 1;
        marker = new ListMarker(ordered, start, indent, indent + bullet.Length + spaces, content);
        return true;
    }

    private static int NextNonBlank(IReadOnlyList<string> lines, int i)
    {
        while (i < lines.Count && IsBlank(lines[i]))
        {
            i++;
        }

        return i;
    }

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ')
        {
            n++;
        }

        return n;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    [GeneratedRegex(@"^( {0,3})(`{3,}|~{3,})([^`]*)$")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ ]+(.*?))?(?:[ ]+#+)?[ ]*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^ {0,3}(?:(?:\*[ ]*){3,}|(?:-[ ]*){3,}|(?:_[ ]*){3,})$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"^ {0,3}> ?")]
    private static partial Regex BlockQuote();

    // A marker must be followed by a space or end of line ("**bold**" is not a list).
    [GeneratedRegex(@"^( {0,3})([-*+]|\d{1,9}[.)])( +|$)(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex TableDelimiter();
}
