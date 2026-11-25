using DevToolsMcp.Server.Core.Markdown;

namespace DevToolsMcp.Tests.Core;

public class MarkdownParserTests
{
    [Fact]
    public void Parses_block_structure()
    {
        var blocks = MarkdownParser.Parse("# H\n\npara\nline two\n\n```sh\nls\n```\n\n> q\n\n- a\n- b\n\n| x | y |\n|---|---|\n| 1 | 2 |\n\n***");
        Assert.Collection(blocks,
            b => Assert.Equal(1, Assert.IsType<MdHeading>(b).Level),
            b => Assert.Equal("para line two", Assert.IsType<MdText>(Assert.Single(Assert.IsType<MdParagraph>(b).Content)).Text),
            b => Assert.Equal(("sh", "ls"), (Assert.IsType<MdCodeBlock>(b).Language, ((MdCodeBlock)b).Code)),
            b => Assert.IsType<MdBlockQuote>(b),
            b => Assert.Equal(2, Assert.IsType<MdList>(b).Items.Count),
            b => Assert.Single(Assert.IsType<MdTable>(b).Rows),
            b => Assert.IsType<MdThematicBreak>(b));
    }

    [Fact]
    public void Loose_list_with_blank_lines_stays_one_list()
    {
        var list = Assert.IsType<MdList>(Assert.Single(MarkdownParser.Parse("1. a\n\n2. b\n\n   continued\n3. c")));
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(2, list.Items[1].Blocks.Count);
    }

    [Fact]
    public void Hard_breaks_and_escapes()
    {
        var inlines = InlineParser.Parse("a\\*b\\_c\nd");
        Assert.Equal(3, inlines.Count);
        Assert.Equal("a*b_c", Assert.IsType<MdText>(inlines[0]).Text);
        Assert.IsType<MdLineBreak>(inlines[1]);
        Assert.Equal("d", Assert.IsType<MdText>(inlines[2]).Text);
    }

    [Fact]
    public void Unmatched_delimiters_are_literal()
    {
        var text = string.Concat(InlineParser.Parse("2 * 3 and **open and [x] and `tick").Select(i => ((MdText)i).Text));
        Assert.Equal("2 * 3 and **open and [x] and `tick", text);
    }
}
