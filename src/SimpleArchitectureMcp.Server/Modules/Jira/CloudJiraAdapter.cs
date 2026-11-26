namespace SimpleArchitectureMcp.Server.Modules.Jira;

/// <summary>Jira Cloud: REST API v2 (wiki markup bodies), enhanced JQL search, users by <c>accountId</c>.</summary>
public sealed class CloudJiraAdapter(IHttpClientFactory httpClientFactory, JiraOptions options)
    : JiraAdapterBase(httpClientFactory, options.GetApiBaseAddress(JiraDeploymentType.Cloud),
        options.CreateAuthorization(JiraDeploymentType.Cloud))
{
    public override JiraDeploymentType DeploymentType => JiraDeploymentType.Cloud;

    /// <summary>Cloud removed <c>/search</c>; <c>/search/jql</c> pages with nextPageToken and returns no total.</summary>
    public override Task<JiraSearchResponse> SearchAsync(string jql, int limit, string? cursor, CancellationToken ct, string fields = SearchFields) =>
        GetAsync($"{Api}search/jql?jql={Uri.EscapeDataString(jql)}&maxResults={limit}&fields={fields}"
            + (string.IsNullOrEmpty(cursor) ? string.Empty : "&nextPageToken=" + Uri.EscapeDataString(cursor)),
            JiraJsonContext.Default.JiraSearchResponse, ct);

    public override async Task<IReadOnlyList<JiraUser>> FindUsersAsync(string query, CancellationToken ct) =>
        await GetAsync($"{Api}user/search?query={Uri.EscapeDataString(query)}&maxResults=10",
            JiraJsonContext.Default.ListJiraUser, ct).ConfigureAwait(false);

    public override JiraUserRef ToRef(JiraUser user) => new() { AccountId = user.AccountId };

    public override Task AssignAsync(string key, JiraUser? user, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Api}issue/{Uri.EscapeDataString(key)}/assignee",
            new JiraCloudAssignRequest { AccountId = user?.AccountId }, JiraJsonContext.Default.JiraCloudAssignRequest, ct);
}
