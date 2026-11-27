using SimpleArchitectureMcp.Server.Modules.Jira;

namespace SimpleArchitectureMcp.Tests.Jira;

public class JiraWikiConverterTests
{
    [Theory]
    [InlineData("# Title", "h1. Title")]
    [InlineData("**bold** *it* ~~gone~~ `code`", "*bold* _it_ -gone- {{code}}")]
    [InlineData("[site](https://x.io) and <https://y.io>", "[site|https://x.io] and [https://y.io]")]
    [InlineData("![](https://x.io/a.png)", "!https://x.io/a.png!")]
    [InlineData("a_b * c [d] {e} | f!", "a\\_b \\* c \\[d\\] \\{e\\} \\| f\\!")]
    [InlineData("---", "----")]
    [InlineData("> quoted", "{quote}\nquoted\n{quote}")]
    public void Markdown_to_wiki(string markdown, string wiki) => Assert.Equal(wiki, JiraWikiConverter.ToWiki(markdown));

    [Fact]
    public void Lists_nest_with_repeated_markers()
    {
        Assert.Equal("* a\n** b\n**# c\n* d", JiraWikiConverter.ToWiki("- a\n  - b\n    1. c\n- d"));
        Assert.Equal("* \\[x\\] done\n* \\[ \\] todo", JiraWikiConverter.ToWiki("- [x] done\n- [ ] todo"));
    }

    [Fact]
    public void Code_blocks_and_tables()
    {
        Assert.Equal("{code:csharp}\nvar x = a*b;\n{code}", JiraWikiConverter.ToWiki("```csharp\nvar x = a*b;\n```"));
        Assert.Equal("{noformat}\nplain\n{noformat}", JiraWikiConverter.ToWiki("```\nplain\n```"));
        Assert.Equal("||A||B||\n|1|2|", JiraWikiConverter.ToWiki("| A | B |\n|---|---|\n| 1 | 2 |"));
    }

    [Theory]
    [InlineData("h3. Title", "### Title")]
    [InlineData("*bold* _it_ -gone- {{a*b_c}} +ins+", "**bold** *it* ~~gone~~ `a*b_c` +ins+")]
    [InlineData("snake_case_name and 2026-11-25 and -x foo -y", "snake_case_name and 2026-11-25 and -x foo -y")]
    [InlineData("[site|https://x.io] [https://y.io] [~accountid:5b10a] [~jdoe] [^log.txt]",
        "[site](https://x.io) <https://y.io> @5b10a @jdoe `log.txt` (attachment)")]
    [InlineData("!screen.png|thumbnail!", "![](screen.png)")]
    [InlineData("{color:red}alert{color}", "alert")]
    [InlineData("bq. wise", "> wise")]
    [InlineData("# one\n## two\n# four\n* three", "1. one\n   1. two\n2. four\n- three")]
    [InlineData("\\*not bold\\* \\[x\\]", "*not bold* [x]")]
    public void Wiki_to_markdown(string wiki, string markdown) => Assert.Equal(markdown, JiraWikiConverter.ToMarkdown(wiki));

    [Fact]
    public void Code_quote_and_table_blocks_to_markdown()
    {
        Assert.Equal("```java\nint *p = _x_;\n```", JiraWikiConverter.ToMarkdown("{code:language=java|title=T}\nint *p = _x_;\n{code}"));
        Assert.Equal("```\nraw\n```\nafter", JiraWikiConverter.ToMarkdown("{noformat}raw{noformat}\nafter"));
        Assert.Equal("> a\n> **b**", JiraWikiConverter.ToMarkdown("{quote}\na\n*b*\n{quote}"));
        Assert.Equal("Yes:\n> quoted text\nreply", JiraWikiConverter.ToMarkdown("Yes:\n{quote}quoted text{quote}\nreply"));
        Assert.Equal("| A | B |\n| --- | --- |\n| [l](u) | `x|y` |", JiraWikiConverter.ToMarkdown("||A||B||\n|[l|u]|{{x|y}}|"));
        Assert.Equal("| 1 | 2 |\n| --- | --- |", JiraWikiConverter.ToMarkdown("|1|2|"));
        Assert.Equal(string.Empty, JiraWikiConverter.ToMarkdown(null));
    }

    [Theory]
    [InlineData("## Steps\n\n1. Run `make`\n2. See **error** in [log](https://ci/1)\n\n- [x] fixed\n\n> note")]
    [InlineData("Use a_b, 5 * 3 and {braces} [brackets] | pipes!")]
    [InlineData("| Key | Value |\n| --- | --- |\n| a | `b` |")]
    [InlineData("```python\nprint('*hi*')\n```")]
    public void Markdown_round_trips_through_wiki(string markdown) =>
        Assert.Equal(markdown, JiraWikiConverter.ToMarkdown(JiraWikiConverter.ToWiki(markdown)));
}
