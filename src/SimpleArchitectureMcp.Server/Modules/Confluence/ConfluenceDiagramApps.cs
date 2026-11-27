namespace SimpleArchitectureMcp.Server.Modules.Confluence;

/// <summary>
/// Which diagram apps a Confluence offers. Configured (<c>Confluence:DiagramApps</c>) or, with <c>auto</c>, detected
/// once per configuration by rendering each app's macro: a missing app renders as "Unknown macro". That works with
/// ordinary user permissions, unlike the admin-only plugin APIs.
/// </summary>
public sealed class ConfluenceDiagramApps
{
    public const string Drawio = "drawio", PlantUml = "plantuml", Mermaid = "mermaid";

    /// <summary>Macro names per app (Data Center and Cloud vendors use different ones).</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Macros = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Drawio] = ["drawio"],
        [PlantUml] = ["plantuml", "plantumlcloud"],
        [Mermaid] = ["mermaid", "mermaid-cloud", "mermaid-macro"],
    };

    private readonly IReadOnlySet<string>? _configured;
    private readonly Func<IConfluenceApiAdapter> _adapter;
    private readonly Lock _lock = new();
    private Task<IReadOnlySet<string>>? _detected;

    public ConfluenceDiagramApps(string? setting, Func<IConfluenceApiAdapter> adapter)
    {
        _adapter = adapter;
        _configured = Parse(setting);
    }

    /// <summary>auto (null), none (empty) or the listed apps; unknown names are ignored.</summary>
    internal static IReadOnlySet<string>? Parse(string? setting)
    {
        var value = setting?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value == "auto")
        {
            return null;
        }

        return value == "none"
            ? new HashSet<string>()
            : value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Replace("draw.io", Drawio, StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal))
                .Where(Macros.ContainsKey).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>"auto", "none" or the configured list, for status lines.</summary>
    public string Describe() => _configured is null ? "auto" : _configured.Count == 0 ? "none" : string.Join(", ", _configured.Order());

    /// <summary>The available apps. Detection runs once; a failed detection is retried on the next call.</summary>
    public async Task<IReadOnlySet<string>> GetAsync(CancellationToken ct)
    {
        if (_configured is not null)
        {
            return _configured;
        }

        Task<IReadOnlySet<string>> task;
        lock (_lock)
        {
            task = _detected ??= DetectAsync();
        }

        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (_detected == task)
                {
                    _detected = null;
                }
            }

            throw;
        }
    }

    private async Task<IReadOnlySet<string>> DetectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var api = _adapter();

        // A macro that surely doesn't exist shows how this Confluence renders "unknown"; an app's macro that renders
        // the same way (or says "unknown macro") is missing.
        var control = await RenderAsync(api, ControlMacro, timeout.Token).ConfigureAwait(false);
        var probes = Macros.SelectMany(app => app.Value.Select(macro => (App: app.Key, Macro: macro, Html: RenderAsync(api, macro, timeout.Token)))).ToList();
        await Task.WhenAll(probes.Select(p => p.Html)).ConfigureAwait(false);
        return probes.Where(p => IsKnown(p.Html.Result, control.Replace(ControlMacro, p.Macro, StringComparison.Ordinal)))
            .Select(p => p.App).ToHashSet(StringComparer.Ordinal);
    }

    private const string ControlMacro = "simple-architecture-mcp-no-such-macro";

    private static Task<string> RenderAsync(IConfluenceApiAdapter api, string macro, CancellationToken ct) =>
        api.RenderStorageAsync($"""<ac:structured-macro ac:name="{macro}" ac:schema-version="1"/>""", ct);

    internal static bool IsKnown(string html, string unknownHtml) =>
        !string.Equals(html, unknownHtml, StringComparison.Ordinal)
        && !html.Contains("unknown macro", StringComparison.OrdinalIgnoreCase)
        && !html.Contains("unknown-macro", StringComparison.OrdinalIgnoreCase);
}
