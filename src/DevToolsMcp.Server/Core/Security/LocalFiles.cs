namespace DevToolsMcp.Server.Core.Security;

/// <summary>Guards for tools that upload local files.</summary>
public static class LocalFiles
{
    /// <summary>
    /// devtools-mcp's own scratch/output folder (<c>%TEMP%/devtools-mcp</c>, e.g. Archi PNG and .drawio files).
    /// Always allowed, although on Windows it lies under AppData\Local\Temp.
    /// </summary>
    public static string OwnOutputRoot => OwnTempFolder.Path;

    /// <summary>
    /// The server runs with the user's file permissions, so uploads are limited to ordinary files: nothing in a
    /// hidden file or folder (~/.ssh, ~/.aws, ~/.config/devtools-mcp, .env, ...) or in Windows AppData, also after
    /// following symbolic links anywhere along the path. This keeps prompt-injected content from talking the model into uploading credentials.
    /// </summary>
    public static FileInfo ValidateUpload(string? filePath, long maxBytes, Func<long, string> size, string parameter = "file")
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException($"{parameter} is required.");
        }

        var path = filePath.Trim();
        if (path.StartsWith('~'))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        }

        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists)
        {
            throw new ArgumentException($"File not found: {file.FullName}");
        }

        PathGuard.EnsureOrdinary(file.FullName, [OwnOutputRoot], () =>
            new ArgumentException($"Refusing to upload {file.FullName}: files in hidden or application-data folders may hold credentials."));

        if (file.Length > maxBytes)
        {
            throw new ArgumentException($"{file.Name} is {size(file.Length)}; the limit is {size(maxBytes)}.");
        }

        return file;
    }
}
