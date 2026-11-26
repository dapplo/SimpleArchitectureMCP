using Microsoft.Extensions.Configuration;
using SimpleArchitectureMcp.Server.Core.Settings;

namespace SimpleArchitectureMcp.Tests.Core;

public sealed class LegacyNamesTests
{
    [Fact]
    public void OldKeysApplyOnlyWhereTheNewKeyIsUnset()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DevToolsMcp:Port"] = "5190",
            ["DevToolsMcp:Tools"] = "jira",
            ["SimpleArchitectureMcp:Tools"] = "github",
        });

        LegacyNames.AddFallback(config, config);

        Assert.Equal("5190", config["SimpleArchitectureMcp:Port"]);
        Assert.Equal("github", config["SimpleArchitectureMcp:Tools"]);
    }

    [Fact]
    public void CopiesTheOldFolderOnceAndLeavesItInPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}");
        var old = Path.Combine(root, LegacyNames.Folder);
        var target = Path.Combine(root, "simple-architecture-mcp");
        Directory.CreateDirectory(old);
        try
        {
            File.WriteAllText(Path.Combine(old, "settings.json"), "{\"Jira\":{\"BaseUrl\":\"https://jira\"}}");

            Assert.Equal(old, LegacyNames.MigrateFolder(target));
            Assert.Contains("https://jira", File.ReadAllText(Path.Combine(target, "settings.json")));
            Assert.True(File.Exists(Path.Combine(old, "settings.json")));

            File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
            Assert.Null(LegacyNames.MigrateFolder(target));   // the new folder exists: never copy again
            Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NoOldFolderNothingToDo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}");
        Assert.Null(LegacyNames.MigrateFolder(Path.Combine(root, "simple-architecture-mcp")));
        Assert.False(Directory.Exists(root));
    }
}
