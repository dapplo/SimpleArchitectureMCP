using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using SimpleArchitectureMcp.Server.Core.Http;
using SimpleArchitectureMcp.Server.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace SimpleArchitectureMcp.Tests.Core;

public class PayloadMetricsHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Records_the_body_size_without_changing_the_body(bool knownLength)
    {
        var module = "metrics-test-" + knownLength;
        var body = "{\"value\":\"" + new string('x', 100_000) + "\"}";
        var recorded = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DevToolsTelemetry.MeterName && instrument.Name == "simple_architecture_mcp.http.client.response.body.size")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "simple_architecture_mcp.module" } && Equals(tag.Value, module))
                {
                    lock (recorded)
                    {
                        recorded.Add(value);
                    }
                }
            }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleHttpClient(module, TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new Canned(Encoding.UTF8.GetBytes(body), knownLength));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(module);

        using var response = await client.GetAsync("http://example.test/", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(body, await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal([body.Length], recorded);
    }

    private sealed class Canned(byte[] body, bool knownLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = knownLength ? new ByteArrayContent(body) : new StreamContent(new NonSeekable(body)),
            });
    }

    private sealed class NonSeekable(byte[] body) : MemoryStream(body, writable: false)
    {
        public override bool CanSeek => false;
    }
}
