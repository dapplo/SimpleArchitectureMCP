using System.Text.RegularExpressions;

namespace DevToolsMcp.Server.Modules.GitHub;

/// <summary>Bound from the <c>GitHub</c> configuration section (env: <c>GitHub__ApiToken</c>, ...).</summary>
public sealed partial class GitHubOptions
{
    public const string SectionName = "GitHub";
    public const string PublicApi = "https://api.github.com/";

    /// <summary>Personal access token (classic or fine-grained). Required.</summary>
    public string? ApiToken { get; set; }

    /// <summary>Empty for github.com. GitHub Enterprise Server: https://host (the <c>/api/v3</c> suffix is optional).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Optional <c>owner/repo</c> used when a tool call omits <c>repo</c>.</summary>
    public string? DefaultRepo { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public bool TryValidate(out string? reason)
    {
        if (string.IsNullOrWhiteSpace(ApiToken))
        {
            reason = "GitHub:ApiToken not set";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(BaseUrl)
            && (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            reason = "GitHub:BaseUrl is not an absolute http(s) URL";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(DefaultRepo) && !RepoName().IsMatch(DefaultRepo.Trim()))
        {
            reason = "GitHub:DefaultRepo must look like owner/repo";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// REST API root (always ends with '/'): https://api.github.com/ for github.com (or an empty URL),
    /// otherwise https://host[/path]/api/v3/ for GitHub Enterprise Server.
    /// </summary>
    public Uri GetApiBaseAddress()
    {
        var url = BaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            return new Uri(PublicApi);
        }

        var host = new Uri(url, UriKind.Absolute).Host.ToLowerInvariant();
        if (host is "github.com" or "api.github.com" or "www.github.com")
        {
            return new Uri(PublicApi);
        }

        if (host.EndsWith(".ghe.com", StringComparison.Ordinal))
        {
            // GitHub Enterprise Cloud with data residency: https://SUBDOMAIN.ghe.com -> https://api.SUBDOMAIN.ghe.com/
            return new Uri(host.StartsWith("api.", StringComparison.Ordinal) ? $"https://{host}/" : $"https://api.{host}/");
        }

        // GitHub Enterprise Server.
        return new Uri(url.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase) ? url + "/" : url + "/api/v3/");
    }

    // "." and ".." are not repository names: in an API path they would climb out of repos/owner/.
    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/(?!\.\.?$)[A-Za-z0-9._-]{1,100}$")]
    internal static partial Regex RepoName();
}
