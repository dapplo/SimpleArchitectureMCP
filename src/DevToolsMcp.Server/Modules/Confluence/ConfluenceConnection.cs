using Microsoft.Extensions.Primitives;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>
/// The current Confluence configuration and adapter. Rebuilt whenever configuration reloads (e.g. after the
/// settings page saves), so the module can be configured or reconfigured without restarting the server.
/// </summary>
public sealed class ConfluenceConnection : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDisposable _subscription;
    private State _state;

    public ConfluenceConnection(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _state = Build();
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, () => Volatile.Write(ref _state, Build()));
    }

    /// <param name="Adapter">null when Confluence is not (validly) configured.</param>
    public sealed record State(IConfluenceApiAdapter? Adapter, string Message);

    public State Current => Volatile.Read(ref _state);

    public IConfluenceApiAdapter Adapter => Current.Adapter ?? throw new ConfluenceNotConfiguredException(Current.Message);

    private State Build()
    {
        var options = new ConfluenceOptions();
        _configuration.GetSection(ConfluenceOptions.SectionName).Bind(options); // source-generated binder (AOT-safe)
        if (!options.TryValidate(out var reason))
        {
            return new State(null, reason!);
        }

        var deployment = options.ResolveDeploymentType();
        return new State(ConfluenceModule.CreateAdapter(deployment, _httpClientFactory, options),
            $"{deployment} · {options.BaseUrl!.Trim().TrimEnd('/')}");
    }

    public void Dispose() => _subscription.Dispose();
}

public sealed class ConfluenceNotConfiguredException(string reason)
    : InvalidOperationException($"Confluence is not configured ({reason}). Open the devtools-mcp settings page.");
