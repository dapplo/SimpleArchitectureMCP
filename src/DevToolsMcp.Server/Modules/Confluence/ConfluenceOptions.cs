using System.Net.Http.Headers;
using DevToolsMcp.Server.Core.Http;

namespace DevToolsMcp.Server.Modules.Confluence;

public enum ConfluenceDeploymentType
{
    /// <summary>Cloud when the host is *.atlassian.net or an email is configured, otherwise Data Center.</summary>
    Auto,
    Cloud,
    DataCenter,
}

/// <summary>Bound from the <c>Confluence</c> configuration section (env: <c>Confluence__BaseUrl</c>, ...).</summary>
public sealed class ConfluenceOptions
{
    public const string SectionName = "Confluence";

    /// <summary>Cloud: https://your-site.atlassian.net (with or without /wiki). Data Center: https://host[/context-path].</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Atlassian account email (Cloud). Leave empty for Data Center personal access tokens.</summary>
    public string? Email { get; set; }

    /// <summary>Cloud API token or Data Center personal access token.</summary>
    public string? ApiToken { get; set; }

    public ConfluenceDeploymentType DeploymentType { get; set; } = ConfluenceDeploymentType.Auto;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Diagram apps installed on this Confluence: <c>auto</c> (default, detected), <c>none</c>, or a list such as
    /// <c>drawio, plantuml, mermaid</c>. Tools that need an app are hidden when it is missing.
    /// </summary>
    public string? DiagramApps { get; set; }

    public bool TryValidate(out string? reason)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiToken))
        {
            reason = "Confluence:BaseUrl and Confluence:ApiToken not set";
            return false;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            reason = "Confluence:BaseUrl is not an absolute http(s) URL";
            return false;
        }

        if (ResolveDeploymentType() == ConfluenceDeploymentType.Cloud && string.IsNullOrWhiteSpace(Email))
        {
            reason = "Confluence:Email is required for Confluence Cloud";
            return false;
        }

        reason = null;
        return true;
    }

    public ConfluenceDeploymentType ResolveDeploymentType()
    {
        if (DeploymentType != ConfluenceDeploymentType.Auto)
        {
            return DeploymentType;
        }

        var isAtlassianHost = Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase);
        return isAtlassianHost || !string.IsNullOrWhiteSpace(Email)
            ? ConfluenceDeploymentType.Cloud
            : ConfluenceDeploymentType.DataCenter;
    }

    /// <summary>
    /// HttpClient base address (always ends with '/'). Cloud: the site root (API paths start with <c>wiki/</c>).
    /// Data Center: the configured URL including any context path.
    /// </summary>
    public Uri GetApiBaseAddress(ConfluenceDeploymentType deployment)
    {
        var url = BaseUrl!.Trim().TrimEnd('/');
        if (deployment == ConfluenceDeploymentType.Cloud && url.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^5];
        }

        return new Uri(url + "/", UriKind.Absolute);
    }

    /// <summary>Cloud: Basic email:token. Data Center: Bearer PAT, or Basic user:password when an email/user is set.</summary>
    public AuthenticationHeaderValue CreateAuthorization(ConfluenceDeploymentType deployment) =>
        deployment == ConfluenceDeploymentType.Cloud || !string.IsNullOrWhiteSpace(Email)
            ? StaticAuthHandler.Basic(Email!, ApiToken!)
            : StaticAuthHandler.Bearer(ApiToken!);
}
