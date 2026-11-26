using DevToolsMcp.Server.Core.Security;

namespace DevToolsMcp.Tests.Core;

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
}
