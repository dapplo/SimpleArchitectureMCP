using System.Text;
using System.Text.Json;

namespace DevToolsMcp.Benchmarks;

/// <summary>Synthetic but realistic payloads: layouts, headings, rich paragraphs, lists, tables and macros.</summary>
internal static class Samples
{
    /// <summary>Confluence storage XHTML of roughly <paramref name="sections"/> × 1.7 KB.</summary>
    public static string ConfluenceStorage(int sections)
    {
        var sb = new StringBuilder();
        sb.Append("<ac:layout>");
        for (var s = 0; s < sections; s++)
        {
            sb.Append("<ac:layout-section ac:type=\"two_equal\"><ac:layout-cell>");
            sb.Append("<h2>Section ").Append(s).Append(" &ndash; overview</h2>");
            sb.Append("<p>This is <strong>important</strong> text with a <a href=\"https://example.com/docs/").Append(s)
                .Append("\">link</a>, some <code>inline_code()</code> and an <em>emphasis</em>. Price: 5 &euro;.</p>");
            sb.Append("<ul><li>First item</li><li>Second <strong>item</strong><ul><li>nested</li></ul></li><li>Third</li></ul>");
            sb.Append("<ac:structured-macro ac:name=\"info\"><ac:parameter ac:name=\"title\">Note</ac:parameter>")
                .Append("<ac:rich-text-body><p>Remember to check the <ac:link><ri:page ri:content-title=\"Runbook\" /></ac:link>.</p></ac:rich-text-body></ac:structured-macro>");
            sb.Append("</ac:layout-cell><ac:layout-cell>");
            sb.Append("<table><tbody><tr><th>Key</th><th>Value</th><th>Owner</th></tr>");
            for (var r = 0; r < 4; r++)
            {
                sb.Append("<tr><td>k").Append(r).Append("</td><td>value ").Append(r).Append(" | piped</td><td><ac:link><ri:user ri:account-id=\"abc")
                    .Append(r).Append("\" /></ac:link></td></tr>");
            }

            sb.Append("</tbody></table>");
            sb.Append("<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">csharp</ac:parameter>")
                .Append("<ac:plain-text-body><![CDATA[var x = Compute(").Append(s).Append(");\nConsole.WriteLine(x);]]></ac:plain-text-body></ac:structured-macro>");
            sb.Append("</ac:layout-cell></ac:layout-section>");
        }

        sb.Append("</ac:layout>");
        return sb.ToString();
    }

    /// <summary>A Data Center v1 content response carrying the given storage body.</summary>
    public static byte[] V1ContentJson(string storage)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", "123");
            w.WriteString("type", "page");
            w.WriteString("title", "Benchmark page");
            w.WriteStartObject("version");
            w.WriteNumber("number", 7);
            w.WriteEndObject();
            w.WriteStartObject("body");
            w.WriteStartObject("storage");
            w.WriteString("value", storage);
            w.WriteString("representation", "storage");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Jira wiki markup of roughly <paramref name="sections"/> × 0.6 KB.</summary>
    public static string JiraWiki(int sections)
    {
        var sb = new StringBuilder();
        for (var s = 0; s < sections; s++)
        {
            sb.Append("h2. Step ").Append(s).Append('\n');
            sb.Append("Some *bold* and _italic_ text with {{code}} and a [link|https://example.com/").Append(s).Append("].\n");
            sb.Append("* first\n* second\n** nested\n# one\n# two\n");
            sb.Append("||Key||Value||\n|a|1|\n|b|2|\n");
            sb.Append("{code:java}\nint x = ").Append(s).Append(";\n{code}\n");
            sb.Append("{quote}quoted text{quote}\n\n");
        }

        return sb.ToString();
    }
}
