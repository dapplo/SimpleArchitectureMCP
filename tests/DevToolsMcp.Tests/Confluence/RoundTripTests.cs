using DevToolsMcp.Server.Modules.Confluence;

namespace DevToolsMcp.Tests.Confluence;

public class RoundTripTests
{
    public static TheoryData<string> Documents =>
    [
        "<h1>Intro</h1><p>Hello <strong>world</strong>, see <a href=\"https://example.com\">docs</a>.</p>",
        "<ul><li>one<ul><li>nested <em>item</em></li></ul></li><li>two</li></ul>",
        "<ol><li>first</li><li>second</li></ol>",
        "<table><tbody><tr><th>K</th><th>V</th></tr><tr><td>a</td><td><code>b|c</code></td></tr></tbody></table>",
        "<ac:structured-macro ac:name=\"code\" ac:schema-version=\"1\"><ac:parameter ac:name=\"language\">python</ac:parameter><ac:plain-text-body><![CDATA[print(\"```\")]]></ac:plain-text-body></ac:structured-macro>",
        "<p>Escapes: 2 * 3 = 6, _x_, `tick`, [brackets], # hash</p>",
        "<ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body>todo</ac:task-body></ac:task></ac:task-list>",
    ];

    /// <summary>storage → markdown → storage → markdown is stable (the Markdown an LLM sees is a fixed point).</summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public void Markdown_is_a_fixed_point(string storage)
    {
        var md1 = ConfluenceMarkupConverter.ToMarkdown(storage);
        var md2 = ConfluenceMarkupConverter.ToMarkdown(ConfluenceMarkupConverter.ToStorage(md1));
        Assert.Equal(md1, md2);
    }

    [Fact]
    public void Storage_round_trips_for_simple_documents()
    {
        const string storage = "<h1>Intro</h1><p>Hello <strong>world</strong></p><ul><li>a<ul><li>b</li></ul></li></ul>";
        Assert.Equal(storage, ConfluenceMarkupConverter.ToStorage(ConfluenceMarkupConverter.ToMarkdown(storage)));
    }

    [Fact]
    public void Simple_macros_are_preserved_through_markdown()
    {
        const string storage =
            "<ac:structured-macro ac:name=\"info\" ac:schema-version=\"1\"><ac:parameter ac:name=\"title\">Heads up</ac:parameter>"
            + "<ac:rich-text-body><p>Read <strong>this</strong>.</p><ul><li>point</li></ul></ac:rich-text-body></ac:structured-macro>"
            + "<p>Status <ac:structured-macro ac:name=\"status\" ac:schema-version=\"1\"><ac:parameter ac:name=\"title\">OK</ac:parameter></ac:structured-macro></p>"
            + "<ac:structured-macro ac:name=\"toc\" ac:schema-version=\"1\"></ac:structured-macro>";

        Assert.Equal(storage, ConfluenceMarkupConverter.ToStorage(ConfluenceMarkupConverter.ToMarkdown(storage)));
    }

    [Fact]
    public void Macro_parameters_with_markdown_characters_survive()
    {
        const string storage = "<ac:structured-macro ac:name=\"jira\" ac:schema-version=\"1\"><ac:parameter ac:name=\"jqlQuery\">project = A AND summary ~ \"*x_\"</ac:parameter></ac:structured-macro>";
        Assert.Equal(storage, ConfluenceMarkupConverter.ToStorage(ConfluenceMarkupConverter.ToMarkdown(storage)));
    }
}
