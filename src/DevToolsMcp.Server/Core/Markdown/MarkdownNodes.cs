namespace DevToolsMcp.Server.Core.Markdown;

// A deliberately small Markdown AST (CommonMark/GFM subset) shared by all modules.
// Each provider renders it into its own markup (Confluence storage XHTML, Jira wiki, ...).

public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Content) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Content) : MdBlock;

/// <summary>Fenced code block. <paramref name="Info"/> is the text after the language on the fence line.</summary>
public sealed record MdCodeBlock(string? Language, string? Info, string Code) : MdBlock;

public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

/// <summary>A list item. <paramref name="Checked"/> is null for normal items, true/false for GFM task items.</summary>
public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked);

public sealed record MdBlockQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public sealed record MdThematicBreak : MdBlock;

public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdStrong(IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdEmphasis(IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdStrike(IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdCode(string Code) : MdInline;

public sealed record MdLink(string Url, IReadOnlyList<MdInline> Content) : MdInline;

public sealed record MdImage(string Url, string Alt) : MdInline;

public sealed record MdLineBreak : MdInline;
