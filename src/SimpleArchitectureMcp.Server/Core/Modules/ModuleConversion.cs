namespace SimpleArchitectureMcp.Server.Core.Modules;

/// <summary>
/// A manual conversion offered on the settings page (for people, not exposed as an MCP tool): the user types a
/// reference (page id, issue key, ...) or picks a file, and the browser downloads the result.
/// </summary>
/// <param name="Input">text (a reference typed by the user) or file (an upload).</param>
/// <param name="Placeholder">Hint for a text input.</param>
/// <param name="Accept">Accepted file extensions for a file input, e.g. ".xml,.archimate".</param>
public sealed record ModuleConversion(string Key, string Label, string Input, string? Placeholder, string? Accept,
    Func<IServiceProvider, ConversionInput, CancellationToken, Task<ConversionFile>> Run);

/// <param name="Text">The typed reference (text input).</param>
/// <param name="FilePath">A temporary copy of the uploaded file, named like the upload (file input). Deleted afterwards.</param>
public sealed record ConversionInput(string? Text, string? FilePath);

public sealed record ConversionFile(string FileName, string ContentType, byte[] Content)
{
    public const string Markdown = "text/markdown; charset=utf-8";

    public static ConversionFile FromMarkdown(string title, string markdown) =>
        new(Security.LocalSavePath.FileName(title, ".md"), Markdown, System.Text.Encoding.UTF8.GetBytes(markdown));
}
