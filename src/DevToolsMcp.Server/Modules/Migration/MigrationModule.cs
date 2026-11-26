using System.Text.Json;
using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Core.Settings;
using DevToolsMcp.Server.Modules.GitHub;
using DevToolsMcp.Server.Modules.Jira;

namespace DevToolsMcp.Server.Modules.Migration;

public static class MigrationModule
{
    public const string Name = "migration";
    public const string ToolPrefix = "migrate_";

    /// <summary>
    /// Registers <c>migrate_issues</c>. Call after <see cref="JiraModule"/> and <see cref="GitHubModule"/>: it uses their
    /// connections and is only listed while both are configured.
    /// </summary>
    public static IMcpServerBuilder AddMigrationModule(this IMcpServerBuilder mcp, IConfiguration configuration, JsonSerializerOptions? jsonOptions = null)
    {
        var services = mcp.Services;
        var settingsFile = configuration["DevToolsMcp:SettingsFile"] is { Length: > 0 } p ? Path.GetFullPath(p) : SettingsStore.DefaultPath();
        var defaultMap = Path.Combine(Path.GetDirectoryName(settingsFile)!, "migration-map.json");

        services.AddSingleton(sp =>
        {
            var jira = sp.GetRequiredService<JiraConnection>();
            var github = sp.GetRequiredService<GitHubConnection>();
            return new MigrationService(() => jira.Adapter, sp.GetRequiredService<JiraService>(), () => github.Api, () => Options(configuration), defaultMap);
        });
        mcp.WithTools<MigrationTools>(jsonOptions);

        ModuleRegistry.From(services).Add(new ModuleDescriptor
        {
            Name = Name,
            Title = "Migration",
            Section = MigrationOptions.SectionName,
            ToolPrefix = ToolPrefix,
            Settings =
            [
                new("UserMap", "User map", "text",
                    Help: "Jira user = GitHub login, comma separated (e.g. jdoe=octocat, jane@acme.com=jane-gh). Unmapped users are named, never assigned or @mentioned."),
                new("Attachments", "Jira attachments on GitHub", "select", Options: ["Link", "Commit"],
                    Help: "Link to the Jira file (needs a Jira login), or commit the files to a branch of the repository."),
                new("AttachmentBranch", "Attachment branch", "text", Help: $"For Commit. Default {MigrationOptions.DefaultBranch}; created when missing."),
                new("MapFile", "Migration record", "text", Help: $"Which issues were copied where. Default {defaultMap}"),
            ],
            Status = sp =>
            {
                var jira = sp.GetRequiredService<JiraConnection>().Current.Adapter is not null;
                var github = sp.GetRequiredService<GitHubConnection>().Current.Api is not null;
                if (!jira || !github)
                {
                    return new ModuleStatus(false, "needs Jira and GitHub configured");
                }

                try
                {
                    var users = Options(configuration).Users();
                    return new ModuleStatus(true, $"Jira ↔ GitHub · {users.Count} mapped users");
                }
                catch (ArgumentException ex)
                {
                    return new ModuleStatus(false, ex.Message);
                }
            },
        });

        return mcp;
    }

    private static MigrationOptions Options(IConfiguration configuration)
    {
        var options = new MigrationOptions();
        configuration.GetSection(MigrationOptions.SectionName).Bind(options); // source-generated binder (AOT-safe)
        return options;
    }
}
