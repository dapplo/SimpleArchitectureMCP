using System.Net;
using System.Text.Json;
using DevToolsMcp.Server.Modules.GitHub;
using DevToolsMcp.Tests.Confluence;

namespace DevToolsMcp.Tests.GitHub;

public class GitHubServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (GitHubService Service, FakeHttp Http) Create(string? defaultRepo = "o/r", string? baseUrl = null)
    {
        var http = new FakeHttp(new Uri("https://unused/"));
        var api = new GitHubApi(http, new GitHubOptions { ApiToken = "tok", BaseUrl = baseUrl, DefaultRepo = defaultRepo });
        return (new GitHubService(api), http);
    }

    private const string Issue = """
        {"number":5,"title":"Crash on save","state":"open","state_reason":null,"body":"Steps\r\n1. save","user":{"login":"bob","id":1},
         "labels":[{"name":"bug","color":"f00"}],"assignees":[{"login":"amy"}],"milestone":{"number":2,"title":"v1"},
         "comments":3,"created_at":"2026-01-02T10:00:00Z","updated_at":"2026-01-05T10:00:00Z",
         "html_url":"https://github.com/o/r/issues/5","repository_url":"https://api.github.com/repos/o/r","reactions":{"+1":3}}
        """;

    [Fact]
    public async Task Search_scopes_to_default_repo_and_prints_short_refs()
    {
        var (svc, http) = Create();
        var q = Uri.EscapeDataString("repo:o/r is:issue is:open label:bug");
        http.On("GET", $"/search/issues?q={q}&per_page=20&sort=updated&order=desc", $$"""{"total_count":7,"items":[{{Issue}}]}""");

        var result = await svc.SearchAsync("is:open label:bug", null, 20, Ct);

        Assert.Equal("#5 | open | Crash on save | bug | @amy | 2026-01-05\n(1 of 7)", result);
    }

    [Fact]
    public async Task Search_keeps_explicit_qualifiers_and_prints_full_refs()
    {
        var (svc, http) = Create();
        var q = Uri.EscapeDataString("org:dapplo is:pr author:@me");
        http.On("GET", $"/search/issues?q={q}&per_page=50&sort=updated&order=desc", $$"""{"total_count":1,"items":[{{Issue}}]}""");

        var result = await svc.SearchAsync("org:dapplo is:pr author:@me", null, 500, Ct);

        Assert.StartsWith("o/r#5 | open |", result);
    }

    [Fact]
    public async Task Get_issue_prints_front_matter_body_and_last_comments()
    {
        var (svc, http) = Create();
        http.On("GET", "/repos/o/r/issues/5", Issue);
        http.On("GET", "/repos/o/r/issues/5/comments?per_page=100&page=1", """
            [{"id":11,"user":{"login":"a"},"body":"one","created_at":"2026-01-03T00:00:00Z"},
             {"id":12,"user":{"login":"b"},"body":"two","created_at":"2026-01-04T00:00:00Z"},
             {"id":13,"user":{"login":"c"},"body":" three ","created_at":"2026-01-05T00:00:00Z"}]
            """);

        var result = await svc.GetIssueAsync(null, 5, 2, Ct);

        Assert.Equal("""
            ---
            issue: o/r#5
            title: Crash on save
            state: open
            author: @bob
            labels: bug
            assignees: @amy
            milestone: v1 (2)
            created: 2026-01-02, updated: 2026-01-05
            url: https://github.com/o/r/issues/5
            comments: 3
            ---
            Steps
            1. save

            ## Comments (last 2 of 3)

            [12] @b 2026-01-04:
            two

            [13] @c 2026-01-05:
            three
            """.ReplaceLineEndings("\n"), result.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Get_issue_with_zero_comments_skips_the_comments_call()
    {
        var (svc, http) = Create();
        http.On("GET", "/repos/o/r/issues/5", Issue);

        await svc.GetIssueAsync("o/r", 5, 0, Ct);

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Get_issue_reads_the_last_two_pages_when_needed()
    {
        var (svc, http) = Create();
        http.On("GET", "/repos/o/r/issues/5", Issue.Replace("\"comments\":3", "\"comments\":101"));
        http.On("GET", "/repos/o/r/issues/5/comments?per_page=100&page=2", """[{"id":200,"body":"last"}]""");
        http.On("GET", "/repos/o/r/issues/5/comments?per_page=100&page=1",
            "[" + string.Join(',', Enumerable.Range(100, 100).Select(i => $$"""{"id":{{i}},"body":"c"}""")) + "]");

        var result = await svc.GetIssueAsync(null, 5, 3, Ct);

        Assert.Contains("## Comments (last 3 of 101)", result);
        Assert.Contains("[198]", result);
        Assert.Contains("[200]", result);
        Assert.DoesNotContain("[197]", result);
    }

    [Fact]
    public async Task Create_sends_only_given_fields()
    {
        var (svc, http) = Create(defaultRepo: null);
        http.On("POST", "/repos/dapplo/SimpleMCP/issues", Issue, HttpStatusCode.Created);

        var result = await svc.CreateIssueAsync("https://github.com/dapplo/SimpleMCP.git", " New ", null, ["bug", "bug", " "], ["@amy"], null, Ct);

        Assert.Equal("Created dapplo/SimpleMCP#5 | https://github.com/o/r/issues/5", result);
        Assert.Equal("""{"title":"New","labels":["bug"],"assignees":["amy"]}""", http.Requests.Single().Body);
    }

    [Fact]
    public async Task Update_closes_relabels_and_reassigns()
    {
        var (svc, http) = Create();
        http.On("PATCH", "/repos/o/r/issues/5", Issue);
        http.On("POST", "/repos/o/r/issues/5/labels", "[]");
        http.On("DELETE", "/repos/o/r/issues/5/labels/needs%20triage", "[]");
        http.On("POST", "/repos/o/r/issues/5/assignees", Issue, HttpStatusCode.Created);
        http.On("DELETE", "/repos/o/r/issues/5/assignees", Issue);

        var result = await svc.UpdateIssueAsync(null, 5, null, null, null, "not_planned", 0,
            ["wontfix"], ["needs triage", "absent"], ["me"], ["amy"], Ct);

        Assert.Equal("Updated o/r#5: closed (not_planned); milestone cleared; +labels wontfix; -labels needs triage, absent; +assignees @me; -assignees @amy.", result);
        Assert.Equal("""{"state":"closed","state_reason":"not_planned","milestone":null}""", http.Requests[0].Body);
        Assert.Equal("""{"labels":["wontfix"]}""", http.Requests[1].Body);
        Assert.Equal("""{"assignees":["amy"]}""", http.Requests[^1].Body);
    }

    [Fact]
    public async Task Update_without_changes_is_rejected()
    {
        var (svc, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateIssueAsync(null, 5, null, null, null, null, null, [], null, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateIssueAsync(null, 5, null, null, "done", null, null, null, null, null, null, Ct));
    }

    [Fact]
    public async Task Comment_adds_or_edits()
    {
        var (svc, http) = Create();
        http.On("POST", "/repos/o/r/issues/5/comments", """{"id":42,"html_url":"https://github.com/o/r/issues/5#issuecomment-42"}""", HttpStatusCode.Created);
        http.On("PATCH", "/repos/o/r/issues/comments/42", """{"id":42,"html_url":"u"}""");

        Assert.Equal("Commented 42 | https://github.com/o/r/issues/5#issuecomment-42", await svc.CommentAsync(null, 5, "LGTM", null, Ct));
        Assert.Equal("Edited comment 42 | u", await svc.CommentAsync(null, 5, "LGTM!", 42, Ct));
        Assert.Equal("""{"body":"LGTM!"}""", http.Requests[1].Body);
    }

    [Fact]
    public async Task Enterprise_server_uses_api_v3_and_github_headers()
    {
        var (svc, http) = Create(baseUrl: "https://ghe.corp");
        http.On("GET", "/api/v3/repos/o/r/labels?per_page=100", """[{"name":"bug","description":"Broken"},{"name":"chore"}]""");

        Assert.Equal("bug | Broken\nchore", await svc.ListLabelsAsync(null, Ct));
        Assert.Equal("/api/v3/repos/o/r/labels?per_page=100", http.Requests.Single().PathAndQuery);
    }

    [Fact]
    public async Task Errors_are_short_and_actionable()
    {
        var (svc, http) = Create();
        http.On("GET", "/repos/o/r/issues/9", """{"message":"Not Found","documentation_url":"https://docs.github.com"}""", HttpStatusCode.NotFound);
        http.On("POST", "/repos/o/r/issues", """{"message":"Validation Failed","errors":[{"resource":"Issue","field":"assignees","code":"invalid"}]}""",
            HttpStatusCode.UnprocessableEntity);

        var notFound = await Assert.ThrowsAsync<GitHubApiException>(() => svc.GetIssueAsync(null, 9, 0, Ct));
        Assert.Equal("GitHub API 404: Not Found (missing, or the token has no access)", notFound.Message);

        var invalid = await Assert.ThrowsAsync<GitHubApiException>(() => svc.CreateIssueAsync(null, "t", null, null, ["ghost"], null, Ct));
        Assert.Equal("GitHub API 422: Validation Failed (assignees invalid)", invalid.Message);
    }

    [Fact]
    public async Task Repo_is_required_without_a_default()
    {
        var (svc, _) = Create(defaultRepo: null);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.ListLabelsAsync(null, Ct));
        Assert.Contains("repo is required", ex.Message);
        Assert.Throws<ArgumentException>(() => GitHubService.ValidateRepo("../etc"));
    }
}
