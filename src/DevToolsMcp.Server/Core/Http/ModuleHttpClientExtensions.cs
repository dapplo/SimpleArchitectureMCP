using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;

namespace DevToolsMcp.Server.Core.Http;

public static class ModuleHttpClientExtensions
{
    /// <summary>
    /// Registers an isolated named HttpClient for an integration module: base address, auth, payload metrics,
    /// and a standard resilience pipeline (retries only for safe/idempotent methods).
    /// HttpClient tracing/metrics are picked up by OpenTelemetry automatically.
    /// </summary>
    public static IHttpClientBuilder AddModuleHttpClient(this IServiceCollection services, string name, Uri baseAddress,
        AuthenticationHeaderValue authorization, TimeSpan timeout)
    {
        var builder = services
            .AddHttpClient(name, client =>
            {
                client.BaseAddress = baseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan; // governed by the resilience pipeline
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("devtools-mcp", "1.0"));
            })
            .AddHttpMessageHandler(() => new StaticAuthHandler(authorization))
            .AddHttpMessageHandler(() => new PayloadMetricsHandler(name));

        builder.AddStandardResilienceHandler(o =>
        {
            o.TotalRequestTimeout.Timeout = timeout;
            o.AttemptTimeout.Timeout = timeout / 2;
            o.CircuitBreaker.SamplingDuration = timeout; // must be >= 2x attempt timeout
            o.Retry.DisableForUnsafeHttpMethods();
        });

        return builder;
    }
}
