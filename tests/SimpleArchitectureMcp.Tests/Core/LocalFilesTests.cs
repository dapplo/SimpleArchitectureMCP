using SimpleArchitectureMcp.Server.Core.Security;

namespace SimpleArchitectureMcp.Tests.Core;

public class LocalFilesTests
{
    private static string Size(long b) => $"{b} B";

    [Fact]
    public void Own_output_folder_is_allowed_even_under_AppData_temp()
    {
        var dir = Directory.CreateDirectory(Path.Combine(LocalFiles.OwnOutputRoot, "lf-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var file = Path.Combine(dir, "d.drawio");
            File.WriteAllText(file, "x");
            Assert.Equal(file, LocalFiles.ValidateUpload(file, 10, Size).FullName);
            Assert.Contains("limit", Assert.Throws<ArgumentException>(() => LocalFiles.ValidateUpload(file, 0, Size)).Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Hidden_folders_elsewhere_are_refused()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), ".hidden-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var file = Path.Combine(dir, "secret.txt");
            File.WriteAllText(file, "x");
            Assert.Contains("hidden", Assert.Throws<ArgumentException>(() => LocalFiles.ValidateUpload(file, 10, Size)).Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A visible folder that is a link into a hidden one, e.g. ~/docs/keys → ~/.ssh.</summary>
    private static (string Root, string Linked)? LinkedHiddenFolder(string? linkParent = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lf-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var hidden = Directory.CreateDirectory(Path.Combine(root, ".secret")).FullName;
        File.WriteAllText(Path.Combine(hidden, "id_ed25519"), "key");
        var linked = Path.Combine(linkParent is null ? root : Directory.CreateDirectory(linkParent).FullName, "docs-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateSymbolicLink(linked, hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(root, recursive: true);
            return null; // Windows without the symlink privilege
        }

        return (root, linked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the link sits in simple-architecture-mcp's own (always allowed) temp folder
    public void Folder_links_into_hidden_folders_are_followed(bool inOwnFolder)
    {
        if (LinkedHiddenFolder(inOwnFolder ? LocalFiles.OwnOutputRoot : null) is not var (root, linked))
        {
            return;
        }

        try
        {
            var file = Path.Combine(linked, "id_ed25519");
            Assert.Contains("hidden", Assert.Throws<ArgumentException>(() => LocalFiles.ValidateUpload(file, 10, Size)).Message);
            Assert.Throws<ArgumentException>(() => LocalSavePath.Validate(Path.Combine(linked, "page.md"), "x.md", [".md"]));
            Assert.Throws<ArgumentException>(() => SimpleArchitectureMcp.Server.Modules.Archi.ArchiPaths.ValidateOutput(Path.Combine(linked, "m.xml"), LocalFiles.OwnOutputRoot, ".xml"));
        }
        finally
        {
            Directory.Delete(linked);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Real_path_follows_links_in_every_folder()
    {
        if (LinkedHiddenFolder() is not var (root, linked))
        {
            return;
        }

        try
        {
            // The temp folder may itself be behind a link (macOS: /var → /private/var).
            var real = PathGuard.Resolve(root);
            Assert.Equal(Path.Combine(real, ".secret", "id_ed25519"), PathGuard.Resolve(Path.Combine(linked, "id_ed25519")));
            Assert.Equal(Path.Combine(real, ".secret", "new", "file.md"), PathGuard.Resolve(Path.Combine(linked, "new", "file.md")));
            Assert.Equal(Path.Combine(real, "plain.txt"), PathGuard.Resolve(Path.Combine(root, "plain.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
