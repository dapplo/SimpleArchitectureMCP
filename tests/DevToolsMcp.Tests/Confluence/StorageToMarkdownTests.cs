using DevToolsMcp.Server.Modules.Confluence;

namespace DevToolsMcp.Tests.Confluence;

public class StorageToMarkdownTests
{
    private static string Md(string storage) => ConfluenceMarkupConverter.ToMarkdown(storage);

    [Fact]
    public void Headings_paragraphs_and_inline_formatting()
    {
        var md = Md("<h1>Title</h1><h3>Sub</h3><p>Plain <strong>bold</strong>, <em>italic</em>, <code>x = 1</code> and <del>gone</del>.</p>");
        Assert.Equal("# Title\n\n### Sub\n\nPlain **bold**, *italic*, `x = 1` and ~~gone~~.", md);
    }

    [Fact]
    public void Links_and_line_breaks()
    {
        var md = Md("<p>See <a href=\"https://example.com/a b\">the docs</a><br />next line</p>");
        Assert.Equal("See [the docs](<https://example.com/a b>)\\\nnext line", md);
    }

    [Fact]
    public void Html_entities_are_decoded_and_nbsp_collapsed()
    {
        Assert.Equal("a \u2014 b & c", Md("<p>a&nbsp;&nbsp;&mdash; b &amp; c</p>"));
    }

    [Fact]
    public void Nested_lists()
    {
        var md = Md("<ul><li>one<ul><li>one.a</li></ul></li><li><p>two</p></li></ul><ol><li>first</li><li>second</li></ol>");
        Assert.Equal("- one\n  - one.a\n- two\n\n1. first\n2. second", md);
    }

    [Fact]
    public void Tables_use_first_row_as_header_and_escape_pipes()
    {
        var md = Md("<table><colgroup><col /></colgroup><tbody><tr><th>Key</th><th>Value</th></tr><tr><td>a|b</td><td><p>x</p><p>y</p></td></tr></tbody></table>");
        Assert.Equal("| Key | Value |\n| --- | --- |\n| a\\|b | x<br>y |", md);
    }

    [Fact]
    public void Code_macro_becomes_fenced_block_with_language()
    {
        var md = Md("<ac:structured-macro ac:name=\"code\" ac:schema-version=\"1\"><ac:parameter ac:name=\"language\">csharp</ac:parameter>"
            + "<ac:parameter ac:name=\"title\">Example file</ac:parameter><ac:plain-text-body><![CDATA[var x = \"<b>\";\nif (a && b) { }]]></ac:plain-text-body></ac:structured-macro>");
        Assert.Equal("```csharp title=\"Example file\"\nvar x = \"<b>\";\nif (a && b) { }\n```", md);
    }

    [Fact]
    public void Rich_body_macros_render_as_quoted_placeholders()
    {
        var md = Md("<ac:structured-macro ac:name=\"warning\"><ac:parameter ac:name=\"title\">Careful</ac:parameter>"
            + "<ac:rich-text-body><p>Do <strong>not</strong> run in prod.</p></ac:rich-text-body></ac:structured-macro>");
        Assert.Equal("> **[Confluence Macro: warning | title=Careful]**\n> Do **not** run in prod.", md);
    }

    [Fact]
    public void Bodyless_and_inline_macros_render_as_placeholders()
    {
        var md = Md("<p>State: <ac:structured-macro ac:name=\"status\"><ac:parameter ac:name=\"colour\">Green</ac:parameter>"
            + "<ac:parameter ac:name=\"title\">DONE</ac:parameter></ac:structured-macro></p>"
            + "<ac:structured-macro ac:name=\"jira\"><ac:parameter ac:name=\"key\">ABC-1</ac:parameter></ac:structured-macro>");
        Assert.Equal("State: **[Confluence Macro: status | colour=Green | title=DONE]**\n\n**[Confluence Macro: jira | key=ABC-1]**", md);
    }

    [Fact]
    public void Confluence_links_images_and_tasks()
    {
        var md = Md("<p><ac:link><ri:page ri:space-key=\"OPS\" ri:content-title=\"Run Book\" /><ac:plain-text-link-body><![CDATA[runbook]]></ac:plain-text-link-body></ac:link> "
            + "<ac:image ac:alt=\"diagram\"><ri:attachment ri:filename=\"arch.png\" /></ac:image></p>"
            + "<ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>ship it</ac:task-body></ac:task>"
            + "<ac:task><ac:task-id>2</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body>test it</ac:task-body></ac:task></ac:task-list>");
        Assert.Equal("[runbook](<page:Run Book?space=OPS>) ![diagram](arch.png)\n\n- [x] ship it\n- [ ] test it", md);
    }

    [Fact]
    public void Markdown_significant_text_is_escaped()
    {
        Assert.Equal("\\# not a heading, 2 \\* 3, snake_case, \\_lead", Md("<p># not a heading, 2 * 3, snake_case, _lead</p>"));
        Assert.Equal("1\\. not a list", Md("<p>1. not a list</p>"));
    }

    [Fact]
    public void Layouts_are_transparent_and_placeholders_dropped()
    {
        var md = Md("<ac:layout><ac:layout-section ac:type=\"two_equal\"><ac:layout-cell><p>left</p></ac:layout-cell>"
            + "<ac:layout-cell><p>right<ac:placeholder>Type here</ac:placeholder></p></ac:layout-cell></ac:layout-section></ac:layout>");
        Assert.Equal("left\n\nright", md);
    }

    [Fact]
    public void Malformed_storage_degrades_to_text()
    {
        Assert.Equal("broken text", Md("<p>broken <b>text</p>"));
    }
}
