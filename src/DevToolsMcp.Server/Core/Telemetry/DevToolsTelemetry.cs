using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DevToolsMcp.Server.Core.Telemetry;

public static class DevToolsTelemetry
{
    public const string ServiceName = "devtools-mcp";
    public const string MeterName = "DevToolsMcp";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<long> ResponseSize = Meter.CreateHistogram<long>(
        "devtools.http.client.response.body.size", "By", "Downstream API response body size.");

    private static readonly Histogram<long> ToolOutputSize = Meter.CreateHistogram<long>(
        "devtools.mcp.tool.output.size", "{char}", "Characters returned to the model per tool call (token proxy).");

    public static void RecordResponseSize(string module, string method, int status, long bytes) =>
        ResponseSize.Record(bytes,
            new KeyValuePair<string, object?>("devtools.module", module),
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("http.response.status_code", status));

    /// <summary>Records the size of a tool result and passes it through.</summary>
    public static string Output(string tool, string result)
    {
        ToolOutputSize.Record(result.Length, new KeyValuePair<string, object?>("mcp.tool.name", tool));
        return result;
    }

    /// <summary>OpenTelemetry tracing + metrics + logs; exported via OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (Aspire sets it).</summary>
    public static WebApplicationBuilder AddDevToolsTelemetry(this WebApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource("ModelContextProtocol"))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(MeterName, "ModelContextProtocol", "System.Runtime"));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
