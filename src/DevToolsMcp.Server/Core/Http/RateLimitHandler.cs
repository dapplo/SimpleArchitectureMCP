using System.Globalization;
using System.Net;

namespace DevToolsMcp.Server.Core.Http;

/// <summary>
/// Waits and retries when a service says "too many requests": HTTP 429, or a 403 with <c>Retry-After</c> or an
/// exhausted <c>X-RateLimit-Remaining</c> (GitHub's primary and secondary limits). A rate-limited request was not
/// processed, so this also retries POST/PUT/PATCH, unlike the standard resilience pipeline. Waits longer than
/// <see cref="MaxWait"/> are not slept through: the 429/403 is returned and the module reports it.
/// </summary>
public sealed class RateLimitHandler : DelegatingHandler
{
    public const int MaxRetries = 3;

    public static TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Delay used when a 429 carries no hint (GitHub asks for at least a minute on secondary limits).</summary>
    internal static TimeSpan DefaultWait { get; set; } = TimeSpan.FromSeconds(5);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (attempt >= MaxRetries || !Replayable(request.Content) || RetryDelay(response, attempt, DateTimeOffset.UtcNow) is not { } wait || wait > MaxWait)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How long to wait before retrying, or null when the response is not a rate limit.</summary>
    internal static TimeSpan? RetryDelay(HttpResponseMessage response, int attempt, DateTimeOffset now)
    {
        var status = response.StatusCode;
        if (status != HttpStatusCode.TooManyRequests && status != HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (response.Headers.RetryAfter is { } retryAfter)
        {
            var delay = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - now : TimeSpan.Zero);
            return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        }

        if (Header(response, "X-RateLimit-Remaining") == "0"
            && long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var reset))
        {
            var delay = DateTimeOffset.FromUnixTimeSeconds(reset) - now + TimeSpan.FromSeconds(1);
            return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        }

        // A plain 403 is a permission problem, not a rate limit.
        return status == HttpStatusCode.TooManyRequests ? DefaultWait * (1 << attempt) : null;
    }

    /// <summary>Buffered bodies (JSON) can be sent again; streamed uploads cannot.</summary>
    private static bool Replayable(HttpContent? content) => content is null or ByteArrayContent;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
