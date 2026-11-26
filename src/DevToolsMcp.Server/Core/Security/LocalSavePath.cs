namespace DevToolsMcp.Server.Core.Security;

/// <summary>Guards for tools that write a file on the user's machine.</summary>
public static class LocalSavePath
{
    /// <summary>
    /// Resolves where to save a file. <paramref name="path"/> may be a file, or a directory (existing, or ending in a
    /// separator) in which case <paramref name="defaultFileName"/> is used. The server runs with the user's file
    /// permissions, so writes are limited the same way uploads are: nothing in a hidden file or folder (~/.ssh,
    /// ~/.config, .git, ...) or in Windows AppData, also after following symbolic links, and only the given
    /// extensions. This keeps prompt-injected content from talking the model into overwriting dotfiles or scripts.
    /// </summary>
    public static FileInfo Validate(string? path, string defaultFileName, IReadOnlyCollection<string> extensions, string parameter = "save_to")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{parameter} is required.");
        }

        var p = path.Trim();
        if (p.StartsWith('~'))
        {
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        }

        if (p.EndsWith(Path.DirectorySeparatorChar) || p.EndsWith(Path.AltDirectorySeparatorChar) || Directory.Exists(p))
        {
            p = Path.Combine(p, defaultFileName);
        }

        var file = new FileInfo(Path.GetFullPath(p));
        if (!extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{parameter} must end in {string.Join(", ", extensions)}.");
        }

        // The path itself and its real path (links followed in every folder along the way).
        PathGuard.EnsureOrdinary(file.FullName, [], () =>
            new ArgumentException($"Refusing to write {file.FullName}: hidden and application-data folders are off limits."));

        return file;
    }

    /// <summary>
    /// Refuses to replace an existing file unless <paramref name="overwrite"/> is set, the file lies in one of
    /// <paramref name="trustedRoots"/> (the tool's own output folder), or its first 4 KB show it is the tool's own
    /// output (<paramref name="isOwnOutput"/>), e.g. an earlier export of a page. This keeps a prompt-injected path
    /// from clobbering pom.xml or notes.md.
    /// </summary>
    public static void EnsureMayReplace(string fullPath, bool overwrite, Func<string, bool> isOwnOutput, params string[] trustedRoots)
    {
        if (overwrite || !File.Exists(fullPath))
        {
            return;
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (trustedRoots.Any(r => fullPath.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar, comparison)))
        {
            return;
        }

        var head = new char[4096];
        int read;
        using (var reader = new StreamReader(fullPath, detectEncodingFromByteOrderMarks: true))
        {
            read = reader.ReadBlock(head, 0, head.Length);
        }

        if (!isOwnOutput(new string(head, 0, read)))
        {
            throw new ArgumentException($"{fullPath} already exists and was not written by devtools-mcp. Pass overwrite=true to replace it.");
        }
    }

    /// <summary>Turns a title into a safe file name (no separators, reserved or control characters).</summary>
    public static string FileName(string title, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string(title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimStart('.').Trim();
        if (name.Length > 120)
        {
            name = name[..120].TrimEnd();
        }

        return (name.Length == 0 ? "page" : name) + extension;
    }
}
