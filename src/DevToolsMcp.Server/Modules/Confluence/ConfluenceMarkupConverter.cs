using System.Text;
using System.Text.RegularExpressions;
using DevToolsMcp.Server.Core.Markdown;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>
/// Deterministic, AOT-safe conversion between Confluence Storage Format (XHTML) and Markdown.
/// Storage → Markdown uses System.Xml.Linq; Markdown → Storage uses the shared <see cref="MarkdownParser"/>
/// and a StringBuilder emitter (no reflection, no third-party libraries).
/// </summary>
/// <remarks>
/// Macros are rendered as <c>**[Confluence Macro: name | key=value]**</c> placeholders (block macros with a rich-text
/// body as a block quote starting with that placeholder). Simple macros round-trip; macros with complex
/// parameters or non-code plain-text bodies do not, which is why tools advertise <c>format=storage</c> for those.
/// </remarks>
public static partial class ConfluenceMarkupConverter
{
    internal const string MacroPrefix = "[Confluence Macro: ";

    /// <summary>Converts Markdown to Confluence Storage Format XHTML.</summary>
    public static string ToStorage(string markdown)
    {
        var emitter = new StorageEmitter();
        emitter.WriteBlocks(MarkdownParser.Parse(markdown));
        return emitter.ToString();
    }

    private sealed class StorageEmitter
    {
        private readonly StringBuilder _sb = new();
        private int _taskId;

        public override string ToString() => _sb.ToString();

        public void WriteBlocks(IEnumerable<MdBlock> blocks)
        {
            foreach (var block in blocks)
            {
                WriteBlock(block);
            }
        }

        private void WriteBlock(MdBlock block)
        {
            switch (block)
            {
                case MdHeading h:
                    _sb.Append("<h").Append(h.Level).Append('>');
                    WriteInlines(h.Content);
                    _sb.Append("</h").Append(h.Level).Append('>');
                    break;

                case MdParagraph p when p.Content is [MdStrong s] && TryParsePlaceholder(s, out var macro):
                    WriteMacro(macro.Name, macro.Parameters, null);
                    break;

                case MdParagraph p:
                    _sb.Append("<p>");
                    WriteInlines(p.Content);
                    _sb.Append("</p>");
                    break;

                case MdCodeBlock code:
                    WriteCodeMacro(code);
                    break;

                case MdList list when list.Items.Count > 0 && list.Items.All(it => it.Checked is not null):
                    _sb.Append("<ac:task-list>");
                    foreach (var item in list.Items)
                    {
                        _sb.Append("<ac:task><ac:task-id>").Append(++_taskId).Append("</ac:task-id><ac:task-status>")
                            .Append(item.Checked == true ? "complete" : "incomplete").Append("</ac:task-status><ac:task-body>");
                        WriteItemContent(item);
                        _sb.Append("</ac:task-body></ac:task>");
                    }

                    _sb.Append("</ac:task-list>");
                    break;

                case MdList list:
                    var tag = list.Ordered ? "ol" : "ul";
                    _sb.Append('<').Append(tag);
                    if (list.Ordered && list.Start != 1)
                    {
                        _sb.Append(" start=\"").Append(list.Start).Append('"');
                    }

                    _sb.Append('>');
                    foreach (var item in list.Items)
                    {
                        _sb.Append("<li>");
                        WriteItemContent(item);
                        _sb.Append("</li>");
                    }

                    _sb.Append("</").Append(tag).Append('>');
                    break;

                case MdBlockQuote quote when quote.Blocks is [MdParagraph first, ..] && first.Content is [MdStrong s, ..] && TryParsePlaceholder(s, out var bodyMacro):
                    var body = new List<MdBlock>();
                    var remainder = first.Content.Skip(1).SkipWhile(n => n is MdLineBreak || n is MdText { Text: var t } && string.IsNullOrWhiteSpace(t)).ToList();
                    if (remainder.Count > 0)
                    {
                        if (remainder[0] is MdText lead)
                        {
                            remainder[0] = new MdText(lead.Text.TrimStart());
                        }

                        body.Add(new MdParagraph(remainder));
                    }

                    body.AddRange(quote.Blocks.Skip(1));
                    WriteMacro(bodyMacro.Name, bodyMacro.Parameters, body);
                    break;

                case MdBlockQuote quote:
                    _sb.Append("<blockquote>");
                    WriteBlocks(quote.Blocks);
                    _sb.Append("</blockquote>");
                    break;

                case MdTable table:
                    _sb.Append("<table><tbody><tr>");
                    foreach (var cell in table.Header)
                    {
                        _sb.Append("<th>");
                        WriteInlines(cell);
                        _sb.Append("</th>");
                    }

                    _sb.Append("</tr>");
                    foreach (var row in table.Rows)
                    {
                        _sb.Append("<tr>");
                        foreach (var cell in row)
                        {
                            _sb.Append("<td>");
                            WriteInlines(cell);
                            _sb.Append("</td>");
                        }

                        _sb.Append("</tr>");
                    }

                    _sb.Append("</tbody></table>");
                    break;

                case MdThematicBreak:
                    _sb.Append("<hr />");
                    break;
            }
        }

        private void WriteItemContent(MdListItem item)
        {
            var blocks = item.Blocks;
            if (blocks.Count > 0 && blocks[0] is MdParagraph lead && blocks.Skip(1).All(b => b is MdList))
            {
                WriteInlines(lead.Content);
                WriteBlocks(blocks.Skip(1));
            }
            else
            {
                WriteBlocks(blocks);
            }
        }

        private void WriteCodeMacro(MdCodeBlock code)
        {
            _sb.Append("<ac:structured-macro ac:name=\"code\" ac:schema-version=\"1\">");
            if (!string.IsNullOrEmpty(code.Language))
            {
                WriteParameter("language", code.Language);
            }

            if (code.Info is not null)
            {
                foreach (Match m in InfoAttribute().Matches(code.Info))
                {
                    WriteParameter(m.Groups[1].Value, m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value);
                }
            }

            _sb.Append("<ac:plain-text-body>");
            AppendCData(code.Code);
            _sb.Append("</ac:plain-text-body></ac:structured-macro>");
        }

        private void WriteMacro(string name, IReadOnlyList<KeyValuePair<string, string>> parameters, IReadOnlyList<MdBlock>? body)
        {
            _sb.Append("<ac:structured-macro ac:name=\"").Append(EscapeAttr(name)).Append("\" ac:schema-version=\"1\">");
            foreach (var (key, value) in parameters)
            {
                WriteParameter(key, value);
            }

            if (body is not null)
            {
                _sb.Append("<ac:rich-text-body>");
                WriteBlocks(body);
                _sb.Append("</ac:rich-text-body>");
            }

            _sb.Append("</ac:structured-macro>");
        }

        private void WriteParameter(string key, string value) =>
            _sb.Append("<ac:parameter ac:name=\"").Append(EscapeAttr(key)).Append("\">").Append(EscapeText(value)).Append("</ac:parameter>");

        private void WriteInlines(IEnumerable<MdInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case MdText t:
                        _sb.Append(EscapeText(t.Text));
                        break;
                    case MdStrong s when TryParsePlaceholder(s, out var macro):
                        WriteMacro(macro.Name, macro.Parameters, null);
                        break;
                    case MdStrong s:
                        Wrap("strong", s.Content);
                        break;
                    case MdEmphasis e:
                        Wrap("em", e.Content);
                        break;
                    case MdStrike st:
                        Wrap("del", st.Content);
                        break;
                    case MdCode c:
                        _sb.Append("<code>").Append(EscapeText(c.Code)).Append("</code>");
                        break;
                    case MdLink l:
                        WriteLink(l);
                        break;
                    case MdImage img:
                        _sb.Append("<ac:image");
                        if (img.Alt.Length > 0)
                        {
                            _sb.Append(" ac:alt=\"").Append(EscapeAttr(img.Alt)).Append('"');
                        }

                        _sb.Append('>');
                        if (IsAbsoluteUrl(img.Url))
                        {
                            _sb.Append("<ri:url ri:value=\"").Append(EscapeAttr(img.Url)).Append("\" />");
                        }
                        else
                        {
                            _sb.Append("<ri:attachment ri:filename=\"").Append(EscapeAttr(StripScheme(img.Url, "attachment:"))).Append("\" />");
                        }

                        _sb.Append("</ac:image>");
                        break;
                    case MdLineBreak:
                        _sb.Append("<br />");
                        break;
                }
            }
        }

        private void WriteLink(MdLink link)
        {
            var url = link.Url;
            var label = PlainText(link.Content);
            if (url.StartsWith("page:", StringComparison.Ordinal))
            {
                var target = url[5..];
                string? space = null;
                var q = target.LastIndexOf("?space=", StringComparison.Ordinal);
                if (q >= 0)
                {
                    space = target[(q + 7)..];
                    target = target[..q];
                }

                _sb.Append("<ac:link><ri:page");
                if (!string.IsNullOrEmpty(space))
                {
                    _sb.Append(" ri:space-key=\"").Append(EscapeAttr(space)).Append('"');
                }

                _sb.Append(" ri:content-title=\"").Append(EscapeAttr(target)).Append("\" />");
                AppendLinkBody(label, target);
                _sb.Append("</ac:link>");
            }
            else if (url.StartsWith("attachment:", StringComparison.Ordinal))
            {
                var file = url[11..];
                _sb.Append("<ac:link><ri:attachment ri:filename=\"").Append(EscapeAttr(file)).Append("\" />");
                AppendLinkBody(label, file);
                _sb.Append("</ac:link>");
            }
            else if (url.StartsWith("user:", StringComparison.Ordinal) && url.IndexOf('=') is var eq and > 5)
            {
                _sb.Append("<ac:link><ri:user ri:").Append(EscapeAttr(url[5..eq])).Append("=\"").Append(EscapeAttr(url[(eq + 1)..])).Append("\" /></ac:link>");
            }
            else
            {
                _sb.Append("<a href=\"").Append(EscapeAttr(url)).Append("\">");
                WriteInlines(link.Content);
                _sb.Append("</a>");
            }
        }

        private void AppendLinkBody(string label, string defaultLabel)
        {
            if (label.Length > 0 && label != defaultLabel)
            {
                _sb.Append("<ac:plain-text-link-body>");
                AppendCData(label);
                _sb.Append("</ac:plain-text-link-body>");
            }
        }

        private void Wrap(string tag, IReadOnlyList<MdInline> content)
        {
            _sb.Append('<').Append(tag).Append('>');
            WriteInlines(content);
            _sb.Append("</").Append(tag).Append('>');
        }

        private void AppendCData(string text) =>
            _sb.Append("<![CDATA[").Append(text.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal)).Append("]]>");
    }

    internal readonly record struct MacroPlaceholder(string Name, IReadOnlyList<KeyValuePair<string, string>> Parameters);

    /// <summary>Recognises <c>**[Confluence Macro: name | k=v | default]**</c>.</summary>
    internal static bool TryParsePlaceholder(MdStrong strong, out MacroPlaceholder macro)
    {
        macro = default;
        if (strong.Content is not [MdText { Text: var text }] || !text.StartsWith(MacroPrefix, StringComparison.Ordinal) || !text.EndsWith(']'))
        {
            return false;
        }

        var parts = text[MacroPrefix.Length..^1].Split(" | ");
        var name = parts[0].Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var parameters = new List<KeyValuePair<string, string>>(parts.Length - 1);
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=');
            parameters.Add(eq < 0 ? new(string.Empty, part) : new(part[..eq], part[(eq + 1)..]));
        }

        macro = new MacroPlaceholder(name, parameters);
        return true;
    }

    private static string PlainText(IEnumerable<MdInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var n in inlines)
        {
            sb.Append(n switch
            {
                MdText t => t.Text,
                MdCode c => c.Code,
                MdStrong s => PlainText(s.Content),
                MdEmphasis e => PlainText(e.Content),
                MdStrike st => PlainText(st.Content),
                MdLink l => PlainText(l.Content),
                MdImage i => i.Alt,
                _ => " ",
            });
        }

        return sb.ToString();
    }

    private static bool IsAbsoluteUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string StripScheme(string url, string scheme) =>
        url.StartsWith(scheme, StringComparison.Ordinal) ? url[scheme.Length..] : url;

    internal static string EscapeText(string s)
    {
        if (s.AsSpan().IndexOfAny('&', '<', '>') < 0)
        {
            return s;
        }

        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    internal static string EscapeAttr(string s) => EscapeText(s).Replace("\"", "&quot;");

    // key=value or key="value with spaces" on a fence info line, e.g. ```java title="Example" linenumbers=true
    [GeneratedRegex("""([A-Za-z][\w-]*)=(?:"([^"]*)"|(\S+))""")]
    private static partial Regex InfoAttribute();
}
