using System.Net;
using System.Net.Http.Headers;
using SimpleArchitectureMcp.Server.Core.Http;

namespace SimpleArchitectureMcp.Tests.Core;

public class RateLimitHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retry_after_wins()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), RateLimitHandler.RetryDelay(response, 0, Now));
    }

    [Fact]
    public void Exhausted_github_quota_waits_until_reset()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", Now.AddSeconds(20).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(TimeSpan.FromSeconds(21), RateLimitHandler.RetryDelay(response, 0, Now));
    }

    [Fact]
    public void Plain_forbidden_and_errors_are_not_rate_limits()
    {
        Assert.Null(RateLimitHandler.RetryDelay(new HttpResponseMessage(HttpStatusCode.Forbidden), 0, Now));
        Assert.Null(RateLimitHandler.RetryDelay(new HttpResponseMessage(HttpStatusCode.InternalServerError), 0, Now));
    }

    [Fact]
    public async Task Retries_a_post_after_429()
    {
        var calls = 0;
        var handler = new RateLimitHandler
        {
            InnerHandler = new Stub(() =>
            {
                var r = new HttpResponseMessage(++calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Created);
                if (calls == 1)
                {
                    r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                }

                return r;
            }),
        };
        using var client = new HttpClient(handler);

        using var response = await client.PostAsync("https://example.test/x", new StringContent("{}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, calls);
    }

    private sealed class Stub(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply());
    }
}
