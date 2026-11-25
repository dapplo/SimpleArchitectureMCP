using DevToolsMcp.Server.Core.Telemetry;

namespace DevToolsMcp.Server.Core.Http;

/// <summary>Records downstream response payload sizes per module so they show up in the Aspire dashboard.</summary>
public sealed class PayloadMetricsHandler(string module) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        DevToolsTelemetry.RecordResponseSize(module, request.Method.Method, (int)response.StatusCode,
            response.Content.Headers.ContentLength ?? 0);
        return response;
    }
}
