namespace SimpleArchitectureMcp.Server.Core.Security;

/// <summary>
/// Shared check behind <see cref="LocalFiles"/>, <see cref="LocalSavePath"/> and the Archi output paths: a path, and
/// the real path it leads to once every symbolic link (or junction) along the way is followed, must not be in a hidden
/// file or folder or in Windows AppData. Following only the last component is not enough: <c>~/docs/keys</c> may be a
/// link to <c>~/.ssh</c>, so <c>~/docs/keys/id_ed25519</c> looks harmless until its folder is resolved.
/// </summary>
internal static class PathGuard
{
    private const int MaxLinkDepth = 40;

    private static StringComparison Comparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Throws <paramref name="refuse"/>'s exception when <paramref name="fullPath"/> or its resolved real path is
    /// sensitive. A candidate inside one of <paramref name="trustedRoots"/> (e.g. simple-architecture-mcp's own temp folder,
    /// which lies under AppData on Windows) is allowed.
    /// </summary>
    public static void EnsureOrdinary(string fullPath, IEnumerable<string> trustedRoots, Func<Exception> refuse)
    {
        var roots = trustedRoots.SelectMany(r => new[] { Path.GetFullPath(r), Resolve(Path.GetFullPath(r)) })
            .Select(r => Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar)
            .Distinct()
            .ToList();
        foreach (var candidate in new[] { fullPath, Resolve(fullPath) })
        {
            if (roots.Any(r => candidate.StartsWith(r, Comparison)))
            {
                continue;
            }

            if (IsSensitive(candidate))
            {
                throw refuse();
            }
        }
    }

    /// <summary>True when a segment of the path is hidden (starts with '.') or is AppData.</summary>
    public static bool IsSensitive(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(s => s.StartsWith('.') || s.Equals("AppData", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The real path: every existing component that is a link is replaced by its target, recursively. Components that
    /// don't exist yet (a file about to be written) are kept as they are.
    /// </summary>
    public static string Resolve(string fullPath) => Resolve(fullPath, 0);

    private static string Resolve(string fullPath, int depth)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists && info.LinkTarget is null)
            {
                return Path.Combine([next, .. parts[(i + 1)..]]);
            }

            if (info.LinkTarget is { } target)
            {
                if (depth >= MaxLinkDepth)
                {
                    throw new IOException($"Too many levels of symbolic links: {fullPath}");
                }

                // Relative targets are relative to the link's folder; the target may itself pass through links.
                next = Resolve(Path.GetFullPath(target, current), depth + 1);
            }

            current = next;
        }

        return current;
    }
}
