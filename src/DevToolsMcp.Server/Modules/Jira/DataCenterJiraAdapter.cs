namespace DevToolsMcp.Server.Modules.Jira;

/// <summary>Jira Data Center / Server: REST API v2 under the configured base URL (context path respected), users by <c>name</c>.</summary>
public sealed class DataCenterJiraAdapter(IHttpClientFactory httpClientFactory, JiraOptions options)
    : JiraAdapterBase(httpClientFactory, options.GetApiBaseAddress(JiraDeploymentType.DataCenter),
        options.CreateAuthorization(JiraDeploymentType.DataCenter))
{
    public override JiraDeploymentType DeploymentType => JiraDeploymentType.DataCenter;

    public override Task<JiraSearchResponse> SearchAsync(string jql, int limit, CancellationToken ct) =>
        GetAsync($"{Api}search?jql={Uri.EscapeDataString(jql)}&maxResults={limit}&fields={SearchFields}",
            JiraJsonContext.Default.JiraSearchResponse, ct);

    /// <summary>Matches username, display name and email.</summary>
    public override async Task<IReadOnlyList<JiraUser>> FindUsersAsync(string query, CancellationToken ct) =>
        await GetAsync($"{Api}user/search?username={Uri.EscapeDataString(query)}&maxResults=10",
            JiraJsonContext.Default.ListJiraUser, ct).ConfigureAwait(false);

    public override JiraUserRef ToRef(JiraUser user) => new() { Name = user.Name };

    public override Task AssignAsync(string key, JiraUser? user, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Api}issue/{Uri.EscapeDataString(key)}/assignee",
            new JiraDataCenterAssignRequest { Name = user?.Name }, JiraJsonContext.Default.JiraDataCenterAssignRequest, ct);
}
