namespace SimpleArchitectureMcp.Server.Core.Security;

/// <summary>
/// simple-architecture-mcp's scratch and default output folder (<c>%TEMP%/simple-architecture-mcp</c>). On Linux and macOS the temp folder
/// is shared by all users, so another user could create <c>/tmp/simple-architecture-mcp</c> first (or make it a link) and read or
/// swap the files written there. The folder is therefore only used when it is ours: not a link, and chmod 0700 succeeds
/// (only the owner may chmod). Otherwise a per-user name is tried, then a random one.
/// </summary>
public static class OwnTempFolder
{
    private static readonly Lazy<string> Folder = new(() => Claim(System.IO.Path.GetTempPath(), Environment.UserName), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The folder (created on first use).</summary>
    public static string Path => Folder.Value;

    /// <summary>A new, empty work folder inside <see cref="Path"/>.</summary>
    public static string NewWorkFolder(string prefix) =>
        Directory.CreateDirectory(System.IO.Path.Combine(Path, prefix + Guid.NewGuid().ToString("N")[..12])).FullName;

    internal static string Claim(string tempRoot, string userName)
    {
        var name = System.IO.Path.Combine(tempRoot, "simple-architecture-mcp");
        if (OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(name).FullName; // %TEMP% is per user
        }

        var user = new string(userName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        foreach (var candidate in new[] { name, $"{name}-{user}", $"{name}-{Guid.NewGuid():N}" })
        {
            if (TryClaim(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Cannot create a private simple-architecture-mcp folder in {tempRoot}.");
    }

    /// <summary>Creates <paramref name="dir"/> as 0700, or checks that an existing one is a real folder this user owns.</summary>
    internal static bool TryClaim(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            var info = new DirectoryInfo(dir);
            if (info.LinkTarget is not null || File.Exists(dir))
            {
                return false;
            }

            Directory.CreateDirectory(dir, Private);
            File.SetUnixFileMode(dir, Private); // fails unless we own it; also tightens a folder from an older version
            return File.GetUnixFileMode(dir) == Private;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
