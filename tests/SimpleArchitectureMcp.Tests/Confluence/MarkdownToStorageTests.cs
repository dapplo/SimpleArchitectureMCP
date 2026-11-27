using SimpleArchitectureMcp.Server.Modules.Confluence;

namespace SimpleArchitectureMcp.Tests.Confluence;

public class MarkdownToStorageTests
{
    private static string Storage(string md) => ConfluenceMarkupConverter.ToStorage(md);

    [Fact]
    public void Blocks_and_inlines()
    {
        Assert.Equal("<h2>Title</h2><p>Some <strong>bold</strong> and <em>it</em> with <code>a&lt;b</code> &amp; <a href=\"https://x.y/?a=1&amp;b=2\">link</a></p><hr />",
            Storage("## Title\n\nSome **bold** and _it_ with `a<b` & [link](https://x.y/?a=1&b=2)\n\n---"));
    }

    [Fact]
    public void Fenced_code_becomes_code_macro_with_cdata()
    {
        Assert.Equal("<ac:structured-macro ac:name=\"code\" ac:schema-version=\"1\"><ac:parameter ac:name=\"language\">js</ac:parameter>"
            + "<ac:plain-text-body><![CDATA[if (a]]]]><![CDATA[>b) {}\n<tag/>]]></ac:plain-text-body></ac:structured-macro>",
            Storage("```js\nif (a]]>b) {}\n<tag/>\n```"));
    }

    [Fact]
    public void Lists_nested_and_ordered()
    {
        Assert.Equal("<ul><li>a<ul><li>b</li></ul></li><li>c</li></ul><ol start=\"3\"><li>x</li></ol>",
            Storage("- a\n  - b\n- c\n\n3. x"));
    }

    [Fact]
    public void Task_lists()
    {
        Assert.Equal("<ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>done</ac:task-body></ac:task>"
            + "<ac:task><ac:task-id>2</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body>todo</ac:task-body></ac:task></ac:task-list>",
            Storage("- [x] done\n- [ ] todo"));
    }

    [Fact]
    public void Tables()
    {
        Assert.Equal("<table><tbody><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>x|y<br />z</td></tr></tbody></table>",
            Storage("| A | B |\n|---|:-:|\n| 1 | x\\|y<br>z |"));
    }

    [Fact]
    public void Plain_block_quote_stays_blockquote()
    {
        Assert.Equal("<blockquote><p>quoted</p></blockquote>", Storage("> quoted"));
    }

    [Fact]
    public void Images_and_confluence_links()
    {
        Assert.Equal("<p><ac:image ac:alt=\"d\"><ri:attachment ri:filename=\"a.png\" /></ac:image> "
            + "<ac:image><ri:url ri:value=\"https://x.y/i.png\" /></ac:image> "
            + "<ac:link><ri:page ri:space-key=\"OPS\" ri:content-title=\"Run Book\" /><ac:plain-text-link-body><![CDATA[rb]]></ac:plain-text-link-body></ac:link></p>",
            Storage("![d](a.png) ![](https://x.y/i.png) [rb](<page:Run Book?space=OPS>)"));
    }

    [Fact]
    public void Emphasis_edge_cases()
    {
        Assert.Equal("<p><strong><em>both</em></strong> snake_case_name 2 * 3 <strong>a <em>b</em></strong></p>",
            Storage("***both*** snake_case_name 2 * 3 **a *b***"));
    }
}
