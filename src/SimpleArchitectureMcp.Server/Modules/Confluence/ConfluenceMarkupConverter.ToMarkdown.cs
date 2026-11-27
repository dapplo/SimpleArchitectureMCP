using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SimpleArchitectureMcp.Server.Core.Markdown;

namespace SimpleArchitectureMcp.Server.Modules.Confluence;

public static partial class ConfluenceMarkupConverter
{
    private static readonly XNamespace Ac = "http://atlassian.com/content";
    private static readonly XNamespace Ri = "http://atlassian.com/resource/identifier";

    private const string HardBreak = "\\\n";

    /// <summary>Converts Confluence Storage Format XHTML to compact Markdown.</summary>
    public static string ToMarkdown(string storage) =>
        string.IsNullOrWhiteSpace(storage) ? string.Empty : ToMarkdown(storage, new StringBuilder()).ToString();

    /// <summary>
    /// Appends the Markdown for <paramref name="storage"/> to <paramref name="output"/>, so callers that add a header
    /// do not copy a large page body once more.
    /// </summary>
    public static StringBuilder ToMarkdown(string storage, StringBuilder output)
    {
        if (string.IsNullOrWhiteSpace(storage))
        {
            return output;
        }

        XElement root;
        try
        {
            root = ParseStorage(storage);
        }
        catch (XmlException)
        {
            // Malformed storage: degrade to plain text rather than failing the tool call.
            return output.Append(Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(storage, " ")), " ").Trim());
        }

        var start = output.Length;
        Join(output, RenderBlockParts(root.Nodes()), loose: true);
        return TrimFrom(output, start);
    }

    internal static XElement ParseStorage(string storage)
    {
        // Storage format uses undeclared ac:/ri: prefixes and HTML named entities; make it well-formed XML.
        // (Regex.Replace returns the same instance when the page has no named entities.)
        var xml = Entity().Replace(storage, static m =>
        {
            var name = m.Groups[1].Value;
            if (name is "amp" or "lt" or "gt" or "quot" or "apos")
            {
                return m.Value;
            }

            var decoded = WebUtility.HtmlDecode(m.Value);
            if (decoded == m.Value)
            {
                return "&amp;" + name + ";";
            }

            var sb = new StringBuilder();
            for (var i = 0; i < decoded.Length; i += char.IsSurrogatePair(decoded, i) ? 2 : 1)
            {
                sb.Append("&#").Append(char.ConvertToUtf32(decoded, i)).Append(';');
            }

            return sb.ToString();
        });

        // Parse as a fragment with the prefixes predeclared, instead of wrapping the (possibly large) body in a root
        // element string.
        var names = new NameTable();
        var namespaces = new XmlNamespaceManager(names);
        namespaces.AddNamespace("ac", Ac.NamespaceName);
        namespaces.AddNamespace("ri", Ri.NamespaceName);
        namespaces.AddNamespace("at", "http://atlassian.com/template");
        var context = new XmlParserContext(names, namespaces, null, XmlSpace.None);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, ConformanceLevel = ConformanceLevel.Fragment };

        var root = new XElement("root");
        using var reader = XmlReader.Create(new StringReader(xml), settings, context);
        reader.Read();
        while (!reader.EOF)
        {
            root.Add(XNode.ReadFrom(reader)); // advances the reader past the node
        }

        return root;
    }

    /// <summary>Removes leading and trailing whitespace of what was appended after <paramref name="start"/>.</summary>
    private static StringBuilder TrimFrom(StringBuilder sb, int start)
    {
        var end = sb.Length;
        while (end > start && char.IsWhiteSpace(sb[end - 1]))
        {
            end--;
        }

        sb.Length = end;
        var lead = start;
        while (lead < end && char.IsWhiteSpace(sb[lead]))
        {
            lead++;
        }

        return lead > start ? sb.Remove(start, lead - start) : sb;
    }

    private readonly record struct Part(string Text, bool IsList);

    private static string Join(List<Part> parts, bool loose) => Join(new StringBuilder(), parts, loose).ToString();

    private static StringBuilder Join(StringBuilder sb, List<Part> parts, bool loose)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                // Inside list items keep nested lists tight; everything else is separated by a blank line.
                sb.Append(!loose && parts[i].IsList ? "\n" : "\n\n");
            }

            sb.Append(parts[i].Text);
        }

        return sb;
    }

    /// <param name="flatten">
    /// The caller joins the parts with blank lines (<c>loose</c>). Plain containers (layouts, divs, ...) then add their
    /// children as separate parts instead of one pre-joined string, which gives the same text without copying a large
    /// page once per nesting level.
    /// </param>
    private static List<Part> RenderBlockParts(IEnumerable<XNode> nodes, bool flatten = true, List<Part>? parts = null)
    {
        parts ??= [];
        var inline = new StringBuilder();

        void FlushInline()
        {
            var text = CleanInline(inline.ToString());
            inline.Clear();
            if (text.Length > 0)
            {
                parts.Add(new Part(EscapeLineStarts(text), false));
            }
        }

        foreach (var node in nodes)
        {
            if (node is XElement e && IsBlock(e))
            {
                FlushInline();
                if (flatten && IsContainer(e))
                {
                    RenderBlockParts(e.Nodes(), flatten: true, parts);
                    continue;
                }

                var block = RenderBlock(e);
                if (block.Length > 0)
                {
                    parts.Add(new Part(block, e.Name.LocalName is "ul" or "ol" or "task-list"));
                }
            }
            else
            {
                inline.Append(RenderInline(node));
            }
        }

        FlushInline();
        return parts;
    }

    /// <summary>Block elements rendered as nothing but their children joined by blank lines (see <see cref="RenderBlock"/>).</summary>
    private static bool IsContainer(XElement e) =>
        e.Name.Namespace == Ac
            ? e.Name.LocalName is "layout" or "layout-section" or "layout-cell"
            : e.Name.LocalName switch
            {
                "p" => e.Elements().Any(IsBlock),
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol" or "table" or "pre" or "blockquote" or "hr" => false,
                _ => true, // div, section, article, header, footer, dl
            };

    private static bool IsBlock(XElement e)
    {
        if (e.Name.Namespace == Ac)
        {
            return e.Name.LocalName switch
            {
                "layout" or "layout-section" or "layout-cell" or "task-list" => true,
                "structured-macro" or "macro" => e.Element(Ac + "rich-text-body") is not null || e.Element(Ac + "plain-text-body") is not null,
                _ => false,
            };
        }

        return e.Name.Namespace == XNamespace.None && e.Name.LocalName switch
        {
            "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol" or "table" or "pre" or "blockquote"
                or "hr" or "div" or "section" or "article" or "header" or "footer" or "dl" => true,
            _ => false,
        };
    }

    private static string RenderBlock(XElement e)
    {
        var name = e.Name.LocalName;
        if (e.Name.Namespace == Ac)
        {
            return name switch
            {
                "task-list" => RenderTaskList(e),
                "structured-macro" or "macro" => RenderMacroBlock(e),
                _ => Join(RenderBlockParts(e.Nodes()), loose: true),
            };
        }

        switch (name)
        {
            case "p":
                return e.Elements().Any(IsBlock) ? Join(RenderBlockParts(e.Nodes()), loose: true) : EscapeLineStarts(CleanInline(RenderInlineChildren(e)));
            case ['h', var level and >= '1' and <= '6']:
                var heading = CleanInline(RenderInlineChildren(e)).Replace(HardBreak, " ");
                return heading.Length == 0 ? string.Empty : new string('#', level - '0') + " " + heading;
            case "ul" or "ol":
                return RenderList(e, name == "ol");
            case "table":
                return RenderTable(e);
            case "pre":
                return Fence(e.Value, null, null);
            case "blockquote":
                return Prefix(Join(RenderBlockParts(e.Nodes()), loose: true), "> ");
            case "hr":
                return "---";
            default:
                return Join(RenderBlockParts(e.Nodes()), loose: true);
        }
    }

    private static string RenderList(XElement list, bool ordered)
    {
        var n = int.TryParse((string?)list.Attribute("start"), out var start) ? start : 1;
        var sb = new StringBuilder();
        foreach (var li in list.Elements().Where(x => x.Name.LocalName == "li"))
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            var marker = ordered ? $"{n++}. " : "- ";
            AppendItem(sb, marker, Join(RenderBlockParts(li.Nodes(), flatten: false), loose: false));
        }

        return sb.ToString();
    }

    private static string RenderTaskList(XElement list)
    {
        var sb = new StringBuilder();
        foreach (var task in list.Elements(Ac + "task"))
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            var done = string.Equals((string?)task.Element(Ac + "task-status"), "complete", StringComparison.OrdinalIgnoreCase);
            var body = task.Element(Ac + "task-body");
            var content = body is null ? string.Empty : Join(RenderBlockParts(body.Nodes(), flatten: false), loose: false);
            AppendItem(sb, done ? "- [x] " : "- [ ] ", content);
        }

        return sb.ToString();
    }

    private static void AppendItem(StringBuilder sb, string marker, string content)
    {
        var pad = new string(' ', marker.StartsWith("- [", StringComparison.Ordinal) ? 2 : marker.Length);
        sb.Append(marker);
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
                if (lines[i].Length > 0)
                {
                    sb.Append(pad);
                }
            }

            sb.Append(lines[i]);
        }
    }

    private static string RenderTable(XElement table)
    {
        var rows = table.Elements()
            .SelectMany(x => x.Name.LocalName is "thead" or "tbody" or "tfoot" ? x.Elements() : [x])
            .Where(x => x.Name.LocalName == "tr")
            .Select(tr => tr.Elements().Where(c => c.Name.LocalName is "th" or "td").Select(RenderCell).ToList())
            .Where(r => r.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = rows.Max(r => r.Count);
        var sb = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append('|');
            for (var c = 0; c < columns; c++)
            {
                var cell = c < rows[r].Count ? rows[r][c] : string.Empty;
                sb.Append(' ').Append(cell).Append(cell.Length > 0 ? " |" : "|");
            }

            sb.Append('\n');
            if (r == 0)
            {
                sb.Append('|');
                for (var c = 0; c < columns; c++)
                {
                    sb.Append(" --- |");
                }

                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderCell(XElement cell)
    {
        var text = string.Join("<br>", RenderBlockParts(cell.Nodes(), flatten: false).Select(p => p.Text));
        return text.Replace(HardBreak, "<br>").Replace("\n", "<br>").Replace("|", "\\|");
    }

    private static string RenderMacroBlock(XElement macro)
    {
        var name = MacroName(macro);
        var parameters = MacroParameters(macro);

        if (name is "code" or "noformat" && macro.Element(Ac + "plain-text-body") is { } codeBody)
        {
            var language = parameters.FirstOrDefault(p => p.Key == "language").Value;
            var info = string.Join(' ', parameters.Where(p => p.Key is not ("language" or ""))
                .Select(p => p.Value.Any(char.IsWhiteSpace) ? $"{p.Key}=\"{p.Value.Replace("\"", "'")}\"" : $"{p.Key}={p.Value}"));
            return Fence(codeBody.Value, language, info);
        }

        var header = Placeholder(name, parameters);
        if (macro.Element(Ac + "rich-text-body") is { } rich)
        {
            var body = Join(RenderBlockParts(rich.Nodes()), loose: true);
            return Prefix(body.Length > 0 ? header + "\n" + body : header, "> ");
        }

        var plain = macro.Element(Ac + "plain-text-body")?.Value ?? string.Empty;
        return Prefix(header + "\n" + Fence(plain, null, null), "> ");
    }

    private static string Placeholder(string name, List<KeyValuePair<string, string>> parameters)
    {
        var sb = new StringBuilder("**").Append(EscapeMarkdown(MacroPrefix)).Append(EscapeMarkdown(name));
        foreach (var (key, value) in parameters)
        {
            sb.Append(" | ");
            if (key.Length > 0)
            {
                sb.Append(EscapeMarkdown(key)).Append('=');
            }
            else if (value.Contains('='))
            {
                sb.Append('=');
            }

            sb.Append(EscapeMarkdown(value));
        }

        return sb.Append("]**").ToString();
    }

    private static string MacroName(XElement macro) => (string?)macro.Attribute(Ac + "name") ?? "unknown";

    private static List<KeyValuePair<string, string>> MacroParameters(XElement macro) =>
        macro.Elements(Ac + "parameter")
            .Select(p => new KeyValuePair<string, string>((string?)p.Attribute(Ac + "name") ?? string.Empty,
                p.HasElements ? DescribeResource(p) : Whitespace().Replace(p.Value, " ").Trim()))
            .ToList();

    private static string DescribeResource(XElement parameter)
    {
        var parts = parameter.Descendants()
            .Where(d => d.Name.Namespace == Ri)
            .Select(d => d.Attributes().FirstOrDefault(a => a.Name.LocalName is "content-title" or "filename" or "space-key" or "value" or "account-id" or "userkey")?.Value)
            .Where(v => !string.IsNullOrEmpty(v));
        var text = string.Join(", ", parts);
        return text.Length > 0 ? text : Whitespace().Replace(parameter.Value, " ").Trim();
    }

    private static string RenderInlineChildren(XElement e)
    {
        var sb = new StringBuilder();
        foreach (var n in e.Nodes())
        {
            sb.Append(RenderInline(n));
        }

        return sb.ToString();
    }

    private static string RenderInline(XNode node)
    {
        switch (node)
        {
            case XText t:
                return EscapeMarkdown(Whitespace().Replace(t.Value, " "));
            case not XElement:
                return string.Empty;
        }

        var e = (XElement)node;
        var name = e.Name.LocalName;

        if (e.Name.Namespace == Ac)
        {
            return name switch
            {
                "link" => RenderAcLink(e),
                "image" => RenderAcImage(e),
                "emoticon" => (string?)e.Attribute(Ac + "emoji-fallback") ?? $":{(string?)e.Attribute(Ac + "name")}:",
                "structured-macro" or "macro" => Placeholder(MacroName(e), MacroParameters(e)),
                "placeholder" or "parameter" => string.Empty,
                "task-list" => " " + RenderTaskList(e) + " ",
                _ => RenderInlineChildren(e),
            };
        }

        if (e.Name.Namespace == Ri)
        {
            return string.Empty;
        }

        return name switch
        {
            "strong" or "b" => WrapInline(RenderInlineChildren(e), "**"),
            "em" or "i" or "cite" => WrapInline(RenderInlineChildren(e), "*"),
            "s" or "del" or "strike" => WrapInline(RenderInlineChildren(e), "~~"),
            "span" when ((string?)e.Attribute("style"))?.Contains("line-through", StringComparison.OrdinalIgnoreCase) == true
                => WrapInline(RenderInlineChildren(e), "~~"),
            "code" or "tt" or "kbd" => CodeSpan(e.Value),
            "a" => Link(CleanInline(RenderInlineChildren(e)), (string?)e.Attribute("href") ?? string.Empty),
            "br" => HardBreak,
            "time" => (string?)e.Attribute("datetime") ?? e.Value,
            "img" => $"![{EscapeMarkdown((string?)e.Attribute("alt") ?? string.Empty)}]({Destination((string?)e.Attribute("src") ?? string.Empty)})",
            _ => RenderInlineChildren(e),
        };
    }

    private static string RenderAcLink(XElement link)
    {
        var body = link.Element(Ac + "plain-text-link-body") is { } plain
            ? EscapeMarkdown(plain.Value)
            : link.Element(Ac + "link-body") is { } rich ? CleanInline(RenderInlineChildren(rich)) : string.Empty;
        var anchor = (string?)link.Attribute(Ac + "anchor");

        string dest;
        string fallback;
        if (link.Element(Ri + "page") is { } page)
        {
            fallback = (string?)page.Attribute(Ri + "content-title") ?? string.Empty;
            var space = (string?)page.Attribute(Ri + "space-key");
            dest = "page:" + fallback + (string.IsNullOrEmpty(space) ? string.Empty : "?space=" + space);
        }
        else if (link.Element(Ri + "attachment") is { } att)
        {
            fallback = (string?)att.Attribute(Ri + "filename") ?? string.Empty;
            dest = "attachment:" + fallback;
        }
        else if (link.Element(Ri + "user") is { } user && user.Attributes().FirstOrDefault(a => a.Name.Namespace == Ri) is { } id)
        {
            fallback = "@user";
            dest = $"user:{id.Name.LocalName}={id.Value}";
        }
        else if (!string.IsNullOrEmpty(anchor))
        {
            fallback = anchor;
            dest = "#" + anchor;
        }
        else
        {
            return body;
        }

        return Link(body.Length > 0 ? body : EscapeMarkdown(fallback), dest);
    }

    private static string RenderAcImage(XElement image)
    {
        var alt = EscapeMarkdown((string?)image.Attribute(Ac + "alt") ?? string.Empty);
        var src = (string?)image.Element(Ri + "url")?.Attribute(Ri + "value")
            ?? (string?)image.Element(Ri + "attachment")?.Attribute(Ri + "filename")
            ?? string.Empty;
        return $"![{alt}]({Destination(src)})";
    }

    private static string Link(string text, string href)
    {
        if (href.Length == 0)
        {
            return text;
        }

        return $"[{(text.Length > 0 ? text : EscapeMarkdown(href))}]({Destination(href)})";
    }

    private static string Destination(string url) =>
        url.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '<' or '>') ? $"<{url.Replace("<", "%3C").Replace(">", "%3E")}>" : url;

    private static string WrapInline(string inner, string marker)
    {
        var trimmed = inner.Trim();
        if (trimmed.Length == 0)
        {
            return inner;
        }

        var lead = inner.Length > 0 && inner[0] == ' ' ? " " : string.Empty;
        var tail = inner.Length > 0 && inner[^1] == ' ' ? " " : string.Empty;
        return lead + marker + trimmed + marker + tail;
    }

    private static string CodeSpan(string code)
    {
        code = Whitespace().Replace(code, " ");
        if (code.Length == 0)
        {
            return string.Empty;
        }

        var longest = LongestRun(code, '`');
        var ticks = new string('`', longest + 1);
        var pad = code[0] == '`' || code[^1] == '`' ? " " : string.Empty;
        return ticks + pad + code + pad + ticks;
    }

    private static string Fence(string code, string? language, string? info)
    {
        code = code.Replace("\r\n", "\n").Trim('\n');
        var fence = new string('`', Math.Max(3, LongestRun(code, '`') + 1));
        var header = string.Join(' ', new[] { language, info }.Where(s => !string.IsNullOrEmpty(s)));
        return $"{fence}{header}\n{code}\n{fence}";
    }

    private static string Prefix(string text, string prefix) =>
        string.Join('\n', text.Split('\n').Select(l => l.Length > 0 ? prefix + l : prefix.TrimEnd()));

    /// <summary>Collapses whitespace around inline content and trims each line.</summary>
    private static string CleanInline(string text)
    {
        text = MultiSpace().Replace(text, " ");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim())).Replace(" \\\n", HardBreak).Trim();
    }

    /// <summary>Escapes characters that would otherwise be parsed as Markdown syntax.</summary>
    internal static string EscapeMarkdown(string text)
    {
        if (text.AsSpan().IndexOfAny("\\*_`[<~|&") < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var escape = c switch
            {
                '\\' or '*' or '`' => true,
                '_' => i == 0 || i == text.Length - 1 || !char.IsLetterOrDigit(text[i - 1]) || !char.IsLetterOrDigit(text[i + 1]),
                '[' => text.IndexOf(']', i) > 0,
                '<' => i + 1 < text.Length && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] == '/'),
                '~' => i + 1 < text.Length && text[i + 1] == '~',
                '&' => HtmlEntities.StartsReference(text, i), // literal "&auml;" stays literal
                _ => false,
            };
            if (escape)
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Escapes block-level markers (#, >, -, +, 1.) at the start of each paragraph line.</summary>
    private static string EscapeLineStarts(string text) =>
        LineStartMarker().Replace(text, static m => m.Groups[1].Value + "\\" + m.Groups[2].Value);

    private static int LongestRun(string s, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in s)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex Entity();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"(?m)^(\d+)?([#>+]|-(?=\s|$)|(?<=\d)[.)])")]
    private static partial Regex LineStartMarker();
}
