using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;

namespace DevToolsMcp.Server.Core.Http;

public static class ModuleHttpClientExtensions
{
    /// <summary>
    /// Registers an isolated named HttpClient for an integration module: payload metrics, a standard resilience
    /// pipeline (retries only for safe/idempotent methods), gzip/deflate/brotli responses and, optionally, a fixed base address and credentials.
    /// Modules whose settings can change at runtime leave those null and set them per adapter/request instead.
    /// HttpClient tracing/metrics are picked up by OpenTelemetry automatically.
    /// </summary>
    public static IHttpClientBuilder AddModuleHttpClient(this IServiceCollection services, string name, TimeSpan timeout,
        Uri? baseAddress = null, AuthenticationHeaderValue? authorization = null)
    {
        var builder = services
            .AddHttpClient(name, client =>
            {
                if (baseAddress is not null)
                {
                    client.BaseAddress = baseAddress;
                }

                client.Timeout = Timeout.InfiniteTimeSpan; // up to the response headers: resilience pipeline; body: the caller's token
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("devtools-mcp", "1.0"));
            })
            .ConfigurePrimaryHttpMessageHandler((handler, _) =>
            {
                // Atlassian and GitHub compress JSON 5-10x; the body is decompressed while it streams.
                switch (handler)
                {
                    case HttpClientHandler h:
                        h.AutomaticDecompression = DecompressionMethods.All;
                        break;
                    case SocketsHttpHandler s:
                        s.AutomaticDecompression = DecompressionMethods.All;
                        break;
                }
            })
            .AddHttpMessageHandler(() => new PayloadMetricsHandler(name));

        if (authorization is not null)
        {
            builder.AddHttpMessageHandler(() => new StaticAuthHandler(authorization));
        }

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
