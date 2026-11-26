using Microsoft.Extensions.Primitives;

namespace SimpleArchitectureMcp.Server.Modules.GitHub;

/// <summary>
/// The current GitHub configuration and API client. Rebuilt whenever configuration reloads (e.g. after the
/// settings page saves), so the module can be configured or reconfigured without restarting the server.
/// </summary>
public sealed class GitHubConnection : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDisposable _subscription;
    private State _state;

    public GitHubConnection(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _state = Build();
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, () => Volatile.Write(ref _state, Build()));
    }

    /// <param name="Api">null when GitHub is not (validly) configured.</param>
    public sealed record State(GitHubApi? Api, string Message);

    public State Current => Volatile.Read(ref _state);

    public GitHubApi Api => Current.Api ?? throw new GitHubNotConfiguredException(Current.Message);

    private State Build()
    {
        var options = new GitHubOptions();
        _configuration.GetSection(GitHubOptions.SectionName).Bind(options); // source-generated binder (AOT-safe)
        if (!options.TryValidate(out var reason))
        {
            return new State(null, reason!);
        }

        var api = new GitHubApi(_httpClientFactory, options);
        var message = api.BaseAddress.Host + (api.DefaultRepo is null ? string.Empty : $" · default {api.DefaultRepo}");
        return new State(api, message);
    }

    public void Dispose() => _subscription.Dispose();
}

public sealed class GitHubNotConfiguredException(string reason)
    : InvalidOperationException($"GitHub is not configured ({reason}). Open the simple-architecture-mcp settings page."), Core.Tools.IToolError;
