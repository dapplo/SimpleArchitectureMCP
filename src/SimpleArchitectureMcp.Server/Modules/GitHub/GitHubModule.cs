using System.Text.Json;
using SimpleArchitectureMcp.Server.Core.Http;
using SimpleArchitectureMcp.Server.Core.Modules;

namespace SimpleArchitectureMcp.Server.Modules.GitHub;

public static class GitHubModule
{
    public const string Name = "github";
    public const string HttpClientName = "github";
    public const string ToolPrefix = "github_";

    /// <summary>
    /// Registers the GitHub issues slice. Its tools are only listed while <c>GitHub:ApiToken</c> is configured
    /// (see <see cref="ModuleToolGate"/>), so an unconfigured module adds zero schema tokens. Everything runs with
    /// the token owner's own permissions; there are no admin tools.
    /// </summary>
    public static IMcpServerBuilder AddGitHubModule(this IMcpServerBuilder mcp, IConfiguration configuration, JsonSerializerOptions? jsonOptions = null)
    {
        var services = mcp.Services;
        var timeout = int.TryParse(configuration["GitHub:TimeoutSeconds"], out var t) ? t : 30;

        services.AddModuleHttpClient(HttpClientName, TimeSpan.FromSeconds(Math.Clamp(timeout, 5, 300)));
        services.AddSingleton(sp => new GitHubConnection(configuration, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<GitHubConnection>();
            return new GitHubService(() => connection.Api);
        });
        mcp.WithTools<GitHubTools>(jsonOptions);

        ModuleRegistry.From(services).Add(new ModuleDescriptor
        {
            Name = Name,
            Title = "GitHub",
            Section = GitHubOptions.SectionName,
            ToolPrefix = ToolPrefix,
            Settings =
            [
                new("ApiToken", "Personal access token", "secret", Required: true,
                    Help: "Fine-grained: Issues read/write (and Metadata read). Classic: repo, or public_repo for public repositories."),
                new("BaseUrl", "Enterprise URL", "url",
                    Help: "Leave empty for github.com. GitHub Enterprise: https://github.example.com or https://SUBDOMAIN.ghe.com"),
                new("DefaultRepo", "Default repository", "text", Help: "Optional owner/repo used when a tool call omits repo."),
            ],
            Status = sp =>
            {
                var state = sp.GetRequiredService<GitHubConnection>().Current;
                return new ModuleStatus(state.Api is not null, state.Message);
            },
            Conversions =
            [
                new("issue-md", "Issue / PR → Markdown (.md)", "text", "owner/repo#123, #123 or URL", null,
                    (sp, input, ct) => sp.GetRequiredService<GitHubService>().DownloadIssueAsync(input.Text!, ct)),
            ],
            Test = async (sp, ct) =>
            {
                var api = sp.GetRequiredService<GitHubConnection>().Api;
                var me = await api.GetViewerAsync(ct).ConfigureAwait(false);
                return $"Connected to {api.BaseAddress.Host} as @{me.Login}.";
            },
        });

        return mcp;
    }
}
