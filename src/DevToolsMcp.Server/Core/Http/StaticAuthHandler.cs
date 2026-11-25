using System.Net.Http.Headers;

namespace DevToolsMcp.Server.Core.Http;

/// <summary>Adds a fixed Authorization header to every outgoing request of a module's HttpClient.</summary>
public sealed class StaticAuthHandler(AuthenticationHeaderValue authorization) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization ??= authorization;
        return base.SendAsync(request, cancellationToken);
    }

    public static AuthenticationHeaderValue Basic(string user, string secret) =>
        new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{secret}")));

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
}
