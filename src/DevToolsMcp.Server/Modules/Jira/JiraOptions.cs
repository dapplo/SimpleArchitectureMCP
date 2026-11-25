using System.Net.Http.Headers;
using DevToolsMcp.Server.Core.Http;

namespace DevToolsMcp.Server.Modules.Jira;

public enum JiraDeploymentType
{
    /// <summary>Cloud when the host is *.atlassian.net or an email is configured, otherwise Data Center.</summary>
    Auto,
    Cloud,
    DataCenter,
}

/// <summary>Bound from the <c>Jira</c> configuration section (env: <c>Jira__BaseUrl</c>, ...).</summary>
public sealed class JiraOptions
{
    public const string SectionName = "Jira";

    /// <summary>Cloud: https://your-site.atlassian.net. Data Center: https://host[/context-path].</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Atlassian account email (Cloud). Leave empty for Data Center personal access tokens.</summary>
    public string? Email { get; set; }

    /// <summary>Cloud API token or Data Center personal access token.</summary>
    public string? ApiToken { get; set; }

    public JiraDeploymentType DeploymentType { get; set; } = JiraDeploymentType.Auto;

    public int TimeoutSeconds { get; set; } = 30;

    public bool TryValidate(out string? reason)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiToken))
        {
            reason = "Jira:BaseUrl and Jira:ApiToken not set";
            return false;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            reason = "Jira:BaseUrl is not an absolute http(s) URL";
            return false;
        }

        if (ResolveDeploymentType() == JiraDeploymentType.Cloud && string.IsNullOrWhiteSpace(Email))
        {
            reason = "Jira:Email is required for Jira Cloud";
            return false;
        }

        reason = null;
        return true;
    }

    public JiraDeploymentType ResolveDeploymentType()
    {
        if (DeploymentType != JiraDeploymentType.Auto)
        {
            return DeploymentType;
        }

        var isAtlassianHost = Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase);
        return isAtlassianHost || !string.IsNullOrWhiteSpace(Email)
            ? JiraDeploymentType.Cloud
            : JiraDeploymentType.DataCenter;
    }

    /// <summary>
    /// HttpClient base address (always ends with '/'). Cloud: the site root (a pasted <c>/jira/...</c> or
    /// <c>/browse/...</c> path is dropped). Data Center: the configured URL including any context path.
    /// </summary>
    public Uri GetApiBaseAddress(JiraDeploymentType deployment)
    {
        var uri = new Uri(BaseUrl!.Trim(), UriKind.Absolute);
        var url = deployment == JiraDeploymentType.Cloud
            ? uri.GetLeftPart(UriPartial.Authority)
            : uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(url.TrimEnd('/') + "/", UriKind.Absolute);
    }

    /// <summary>Cloud: Basic email:token. Data Center: Bearer PAT, or Basic user:password when an email/user is set.</summary>
    public AuthenticationHeaderValue CreateAuthorization(JiraDeploymentType deployment) =>
        deployment == JiraDeploymentType.Cloud || !string.IsNullOrWhiteSpace(Email)
            ? StaticAuthHandler.Basic(Email!, ApiToken!)
            : StaticAuthHandler.Bearer(ApiToken!);
}
