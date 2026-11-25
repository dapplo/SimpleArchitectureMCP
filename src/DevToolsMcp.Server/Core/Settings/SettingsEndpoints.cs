using System.Net;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Core.Settings;

public sealed record SettingView(string Key, string Label, string Kind, bool Required, string? Help, IReadOnlyList<string>? Options, string? Value, bool HasValue);

public sealed record ModuleView(string Name, string Title, bool Active, string Status, IReadOnlyList<SettingView> Settings);

public sealed record ModulesView(string McpUrl, string SettingsFile, IReadOnlyList<ModuleView> Modules);

public sealed record ResultView(bool Ok, string Message);

/// <summary>
/// The built-in settings page (<c>GET /</c>) and its small JSON API. Only reachable via a loopback host name, and
/// cross-origin requests are rejected, so other websites cannot reconfigure the server (or redirect tokens).
/// </summary>
public static class SettingsEndpoints
{
    public static WebApplication MapSettings(this WebApplication app, SettingsStore store, string mcpUrl)
    {
        var registry = app.Services.GetRequiredService<ModuleRegistry>();
        var config = app.Configuration;

        app.MapGet("/", () => Results.Content(SettingsPage.Html, "text/html; charset=utf-8"));

        var api = app.MapGroup("/api/modules").AddEndpointFilter(async (ctx, next) =>
            IsSameOriginLoopback(ctx.HttpContext.Request) ? await next(ctx) : Results.StatusCode(StatusCodes.Status403Forbidden));

        api.MapGet("/", (IServiceProvider sp) =>
            new ModulesView(mcpUrl, store.FilePath, registry.Modules.Select(m => View(m, config, sp)).ToList()));

        api.MapPut("/{name}", (string name, Dictionary<string, string?> values, IServiceProvider sp) =>
        {
            if (registry.Find(name) is not { } module)
            {
                return Results.NotFound(new ResultView(false, $"Unknown module '{name}'."));
            }

            var changes = new Dictionary<string, string?>();
            var urlChanged = false;
            var secretProvided = false;
            foreach (var setting in module.Settings)
            {
                if (!values.TryGetValue(setting.Key, out var value))
                {
                    continue;
                }

                value = value?.Trim();
                var current = config[$"{module.Section}:{setting.Key}"];
                if (setting.Kind == "secret")
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        continue; // empty = keep the stored secret
                    }

                    secretProvided = true;
                }
                else if (setting.Kind == "url" && !string.Equals(value, current, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(value) && !Uri.TryCreate(value, UriKind.Absolute, out _))
                    {
                        return Results.BadRequest(new ResultView(false, $"{setting.Label} must be an absolute URL."));
                    }

                    urlChanged = true;
                }

                changes[setting.Key] = value ?? string.Empty;
            }

            // Never send an existing token to a new host without the user re-entering it.
            var hasStoredSecret = module.Settings.Any(s => s.Kind == "secret" && !string.IsNullOrEmpty(config[$"{module.Section}:{s.Key}"]));
            if (urlChanged && hasStoredSecret && !secretProvided)
            {
                return Results.BadRequest(new ResultView(false, "Re-enter the token when changing the URL."));
            }

            store.Save(module.Section, changes);
            return Results.Ok(View(module, config, sp));
        });

        api.MapPost("/{name}/test", async (string name, IServiceProvider sp, CancellationToken ct) =>
        {
            if (registry.Find(name) is not { Test: { } test } module)
            {
                return Results.NotFound(new ResultView(false, $"Unknown module '{name}'."));
            }

            if (!module.Status(sp).Active)
            {
                return Results.Ok(new ResultView(false, module.Status(sp).Message));
            }

            try
            {
                return Results.Ok(new ResultView(true, await test(sp, ct)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new ResultView(false, ex.Message));
            }
        });

        return app;
    }

    private static ModuleView View(ModuleDescriptor module, IConfiguration config, IServiceProvider sp)
    {
        var status = module.Status(sp);
        var settings = module.Settings.Select(s =>
        {
            var value = config[$"{module.Section}:{s.Key}"];
            var secret = s.Kind == "secret";
            return new SettingView(s.Key, s.Label, s.Kind, s.Required, s.Help, s.Options, secret ? null : value, !string.IsNullOrEmpty(value));
        }).ToList();
        return new ModuleView(module.Name, module.Title, status.Active, status.Message, settings);
    }

    /// <summary>Blocks DNS rebinding (Host must be loopback) and cross-site requests (Origin must match).</summary>
    internal static bool IsSameOriginLoopback(HttpRequest request)
    {
        var host = request.Host.Host;
        var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (!loopback)
        {
            return false;
        }

        string? origin = request.Headers.Origin;
        return string.IsNullOrEmpty(origin)
            || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
