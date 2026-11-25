using System.Text.Json;
using DevToolsMcp.Server.Core.Http;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Modules.Confluence;

public static class ConfluenceModule
{
    public const string Name = "confluence";
    public const string HttpClientName = "confluence";

    /// <summary>
    /// Registers the Confluence slice when <c>Confluence:BaseUrl</c> and <c>Confluence:ApiToken</c> are configured.
    /// When they are not, nothing is registered, so the tools add zero schema tokens to the model's context.
    /// </summary>
    public static IMcpServerBuilder AddConfluenceModule(this IMcpServerBuilder mcp, IConfiguration configuration, JsonSerializerOptions? jsonOptions = null)
    {
        var registry = ModuleRegistry.From(mcp.Services);

        var options = new ConfluenceOptions();
        configuration.GetSection(ConfluenceOptions.SectionName).Bind(options); // source-generated binder (AOT-safe)
        if (!options.TryValidate(out var reason))
        {
            registry.MarkDisabled(Name, reason!);
            return mcp;
        }

        var deployment = options.ResolveDeploymentType();
        var services = mcp.Services;
        services.AddSingleton(options);
        services.AddModuleHttpClient(HttpClientName, options.GetApiBaseAddress(deployment), options.CreateAuthorization(deployment),
            TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300)));

        services.AddSingleton<IConfluenceApiAdapter>(sp => CreateAdapter(deployment, sp.GetRequiredService<IHttpClientFactory>(), options));
        services.AddSingleton<ConfluenceService>();
        mcp.WithTools<ConfluenceTools>(jsonOptions);

        registry.MarkEnabled($"{Name}:{deployment}");
        return mcp;
    }

    internal static IConfluenceApiAdapter CreateAdapter(ConfluenceDeploymentType deployment, IHttpClientFactory factory, ConfluenceOptions options) =>
        deployment == ConfluenceDeploymentType.Cloud
            ? new CloudConfluenceAdapter(factory, options)
            : new DataCenterConfluenceAdapter(factory, options);
}
