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

        // Check the path itself, the file's link target and the link target of every existing parent folder.
        var candidates = new List<string> { file.FullName };
        if (file.Exists && file.ResolveLinkTarget(returnFinalTarget: true) is { } fileTarget)
        {
            candidates.Add(Path.GetFullPath(fileTarget.FullName));
        }

        // Drive and file-system roots are never links, and Windows can't resolve a link target for "D:\\".
        for (var dir = file.Directory; dir?.Parent is not null; dir = dir.Parent)
        {
            if (dir.Exists && dir.ResolveLinkTarget(returnFinalTarget: true) is { } dirTarget)
            {
                candidates.Add(Path.GetFullPath(dirTarget.FullName));
            }
        }

        foreach (var candidate in candidates)
        {
            var segments = candidate.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => s.StartsWith('.') || s.Equals("AppData", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Refusing to write {file.FullName}: hidden and application-data folders are off limits.");
            }
        }

        return file;
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
