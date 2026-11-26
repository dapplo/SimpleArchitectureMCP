using System.Net;
using DevToolsMcp.Server.Modules.GitHub;
using DevToolsMcp.Server.Modules.Jira;
using DevToolsMcp.Server.Modules.Migration;
using DevToolsMcp.Tests.Confluence;
using DevToolsMcp.Tests.Jira;

namespace DevToolsMcp.Tests.Migration;

public sealed class MigrationServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Directory.CreateTempSubdirectory("migration-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MapFile => Path.Combine(_dir, "map.json");

    private (MigrationService Service, JiraFakeHttp Jira, FakeHttp GitHub) Create(string userMap = "Jane Doe=jane-gh, jdoe=jdoe-gh", string? attachments = null)
    {
        var jiraOptions = new JiraOptions { BaseUrl = "https://acme.atlassian.net", Email = "me@acme.com", ApiToken = "t" };
        var jiraHttp = new JiraFakeHttp();
        var adapter = JiraModule.CreateAdapter(jiraOptions.ResolveDeploymentType(), jiraHttp, jiraOptions);
        var ghHttp = new FakeHttp(new Uri("https://unused/"));
        var gh = new GitHubApi(ghHttp, new GitHubOptions { ApiToken = "tok", DefaultRepo = "o/r" });
        var options = new MigrationOptions { UserMap = userMap, MapFile = MapFile, Attachments = attachments };
        var service = new MigrationService(() => adapter, new JiraService(adapter), () => gh, () => options, MapFile) { GitHubWritePause = TimeSpan.Zero };
        return (service, jiraHttp, ghHttp);
    }

    private const string Fields = "summary,description,status,issuetype,priority,resolution,assignee,reporter,labels,parent,attachment,subtasks,issuelinks,fixVersions,components,created,updated";

    private const string JiraIssue = """
        {"key":"OPS-7","fields":{"summary":"Broken build","description":"See [~jdoe] and [~stranger]\n!shot.png!",
         "status":{"name":"Done","statusCategory":{"key":"done"}},"issuetype":{"name":"Bug"},"priority":{"name":"High"},
         "resolution":{"name":"Won't Fix"},"assignee":{"accountId":"a1","displayName":"Jane Doe"},"reporter":{"displayName":"John"},
         "labels":["ci"],"fixVersions":[{"name":"1.0"}],"issuelinks":[{"type":{"name":"Blocks","outward":"blocks","inward":"is blocked by"},"outwardIssue":{"key":"OPS-9"}}],
         "attachment":[{"filename":"shot.png","size":2048,"content":"https://acme.atlassian.net/rest/api/2/attachment/content/9"}],
         "created":"2026-11-01T10:00:00.000+0000"}}
        """;

    private const string JiraComments = """
        {"comments":[{"author":{"displayName":"Bob"},"body":"first *bold*","created":"2026-11-02T09:30:00.000+0000"},
                     {"author":{"displayName":"Amy"},"body":"second","created":"2026-11-03T12:00:00.000+0100"}],"total":2,"startAt":0}
        """;

    private static void JiraSource(JiraFakeHttp jira)
    {
        jira.On("GET", $"/rest/api/2/issue/OPS-7?fields={Fields}", JiraIssue);
        jira.On("GET", "/rest/api/2/issue/OPS-7/comment?orderBy=created&startAt=0&maxResults=100", JiraComments);
    }

    private static void GitHubTarget(FakeHttp gh)
    {
        gh.On("GET", "/repos/o/r", """{"default_branch":"main","permissions":{"push":true}}""");
        gh.On("GET", "/repos/o/r/milestones?state=all&per_page=100", """[{"number":3,"title":"1.0"}]""");
        gh.On("POST", "/repos/o/r/issues", """{"id":900,"number":45,"html_url":"https://github.com/o/r/issues/45"}""", HttpStatusCode.Created);
        gh.On("POST", "/repos/o/r/issues/45/comments", """{"id":1}""", HttpStatusCode.Created);
        gh.On("PATCH", "/repos/o/r/issues/45", """{"number":45}""");
    }

    [Theory]
    [InlineData("""{"default_branch":"main","permissions":{"push":false}}""")] // a public repo anyone may open issues in
    [InlineData("""{"default_branch":"main"}""")]
    public async Task Jira_to_GitHub_refuses_repositories_the_user_cannot_push_to(string repo)
    {
        var (svc, jira, gh) = Create();
        JiraSource(jira);
        GitHubTarget(gh);
        gh.On("GET", "/repos/o/r", repo);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.MigrateAsync("o/r", ["OPS-7"], null, null, 10, false, Ct));

        Assert.Contains("no write access", ex.Message);
        Assert.DoesNotContain(gh.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Jira_to_GitHub_copies_issue_comments_and_status_then_skips_on_rerun()
    {
        var (svc, jira, gh) = Create();
        JiraSource(jira);
        GitHubTarget(gh);

        var result = await svc.MigrateAsync("o/r", ["ops-7"], null, null, 10, false, Ct);

        Assert.Equal("OPS-7 → o/r#45 | 2 comments, 1 attachments | https://github.com/o/r/issues/45", result);
        var create = gh.Requests.Single(r => r is { PathAndQuery: "/repos/o/r/issues" }).Body!;
        var body = Json(create, "body");
        Assert.Contains("\"title\":\"Broken build\"", create);
        Assert.Contains("\"labels\":[\"ci\",\"bug\",\"priority: High\"]", create);
        Assert.Contains("\"assignees\":[\"jane-gh\"]", create);
        Assert.Contains("\"milestone\":3", create);
        Assert.Contains("Migrated from Jira [OPS-7](https://acme.atlassian.net/browse/OPS-7) · Bug · Done (Won't Fix)", body);
        Assert.Contains("See @jdoe-gh and `@stranger`", body); // unmapped users are never pinged
        Assert.Contains("![shot.png](https://acme.atlassian.net/rest/api/2/attachment/content/9)", body);
        Assert.Contains("**Links:** blocks OPS-9", body);

        var comments = gh.Requests.Where(r => r.PathAndQuery == "/repos/o/r/issues/45/comments").Select(r => r.Body!).ToList();
        Assert.Equal(2, comments.Count);
        Assert.Equal("**Bob** commented on 2026-11-02 09:30 UTC (Jira):\n\nfirst **bold**", Json(comments[0], "body"));
        Assert.StartsWith("**Amy** commented on 2026-11-03 11:00 UTC (Jira)", Json(comments[1], "body"));
        Assert.Contains("\"state_reason\":\"not_planned\"", gh.Requests.Single(r => r.Method == HttpMethod.Patch).Body);

        var before = gh.Requests.Count + jira.Requests.Count;
        Assert.Equal("OPS-7 = o/r#45 (already migrated)", await svc.MigrateAsync("o/r", ["OPS-7"], null, null, 10, false, Ct));
        Assert.Equal(before, gh.Requests.Count + jira.Requests.Count);
    }

    [Fact]
    public async Task Interrupted_issue_resumes_with_the_remaining_comments()
    {
        var (svc, jira, gh) = Create();
        JiraSource(jira);
        GitHubTarget(gh);
        File.WriteAllText(MapFile, """
            {"issues":{"jira:acme.atlassian.net/OPS-7 -> github:github.com/o/r":{"target":"o/r#45","url":"https://github.com/o/r/issues/45","targetId":900,"comments":1,"attachments":true}}}
            """);

        var result = await svc.MigrateAsync("o/r", ["OPS-7"], null, null, 10, false, Ct);

        Assert.StartsWith("OPS-7 → o/r#45", result);
        Assert.DoesNotContain(gh.Requests, r => r is { Method.Method: "POST", PathAndQuery: "/repos/o/r/issues" });
        var comment = Assert.Single(gh.Requests, r => r.PathAndQuery == "/repos/o/r/issues/45/comments");
        Assert.Contains("**Amy**", comment.Body);
        Assert.Contains("\"done\": true", File.ReadAllText(MapFile));
    }

    private static readonly DateTimeOffset Started = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Issue_created_by_an_interrupted_run_is_adopted_not_created_again()
    {
        var (svc, jira, gh) = Create();
        JiraSource(jira);
        GitHubTarget(gh);
        gh.On("GET", "/user", """{"login":"me"}""");
        gh.On("GET", "/repos/o/r/issues?creator=me&state=all&sort=created&direction=desc&per_page=100&since=2026-11-26T09%3A59%3A00Z", """
            [{"id":901,"number":46,"body":"> Migrated from Jira [OPS-8](https://acme.atlassian.net/browse/OPS-8)"},
             {"id":900,"number":45,"html_url":"https://github.com/o/r/issues/45","body":"> Migrated from Jira [OPS-7](https://acme.atlassian.net/browse/OPS-7) · Bug"}]
            """);
        File.WriteAllText(MapFile, """
            {"issues":{"jira:acme.atlassian.net/OPS-7 -> github:github.com/o/r":{"creating":"2026-11-26T10:00:00+00:00"}}}
            """);

        var result = await svc.MigrateAsync("o/r", ["OPS-7"], null, null, 10, false, Ct);

        Assert.StartsWith("OPS-7 → o/r#45 | 2 comments", result);
        Assert.DoesNotContain(gh.Requests, r => r is { Method.Method: "POST", PathAndQuery: "/repos/o/r/issues" });
        Assert.Equal(2, gh.Requests.Count(r => r.PathAndQuery == "/repos/o/r/issues/45/comments"));
        Assert.DoesNotContain("creating", File.ReadAllText(MapFile));
    }

    [Fact]
    public async Task Creation_is_marked_before_the_create_request()
    {
        var (svc, jira, gh) = Create();
        svc.Now = () => Started;
        JiraSource(jira);
        GitHubTarget(gh);
        gh.On("POST", "/repos/o/r/issues", """{"message":"Server Error"}""", HttpStatusCode.InternalServerError);

        var result = await svc.MigrateAsync("o/r", ["OPS-7"], null, null, 10, false, Ct);

        Assert.StartsWith("OPS-7 ✗", result);
        Assert.Contains("\"creating\": \"2026-11-26T10:00:00+00:00\"", File.ReadAllText(MapFile)); // a re-run looks before creating
    }

    [Fact]
    public async Task GitHub_to_Jira_adopts_the_issue_of_an_interrupted_run()
    {
        var (svc, jira, gh) = Create();
        svc.Now = () => Started.AddMinutes(30);
        gh.On("GET", "/repos/o/r/issues/5", """
            {"id":5000,"number":5,"title":"Crash","state":"open","body":"x","user":{"login":"bob"},"comments":0,"html_url":"https://github.com/o/r/issues/5"}
            """);
        gh.On("GET", "/repos/o/r/issues/5/sub_issues?per_page=100", "[]");
        var jql = "project = OPS AND creator = currentUser() AND created >= -32m ORDER BY created ASC";
        jira.On("GET", $"/rest/api/2/search/jql?jql={Uri.EscapeDataString(jql)}&maxResults=50&fields=description", """
            {"issues":[{"key":"OPS-10","fields":{"description":"> Migrated from GitHub [o/r#50|https://github.com/o/r/issues/50]"}},
                       {"key":"OPS-11","fields":{"description":"> Migrated from GitHub [o/r#5|https://github.com/o/r/issues/5] · opened by"}}],"isLast":true}
            """);
        jira.On("GET", $"/rest/api/2/issue/OPS-11?fields={Fields}", """{"key":"OPS-11","fields":{}}""");
        File.WriteAllText(MapFile, """
            {"issues":{"github:github.com/o/r#5 -> jira:acme.atlassian.net/OPS":{"creating":"2026-11-26T10:00:00+00:00"}}}
            """);

        var result = await svc.MigrateAsync("OPS", ["#5"], null, null, 10, false, Ct);

        Assert.StartsWith("o/r#5 → OPS-11", result);
        Assert.DoesNotContain(jira.Requests, r => r is { Method.Method: "POST", PathAndQuery: "/rest/api/2/issue" });
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var (svc, jira, gh) = Create();
        JiraSource(jira);

        var result = await svc.MigrateAsync("o/r", ["OPS-7"], null, null, 10, true, Ct);

        Assert.Equal("OPS-7 → o/r (dry run): \"Broken build\" | labels: ci, bug, priority: High | assignee: @jane-gh | closed (not_planned) | milestone: 1.0 | 2 comments | 1 attachments", result);
        Assert.Empty(gh.Requests);
        Assert.False(File.Exists(MapFile));
    }

    [Fact]
    public async Task Query_pages_with_a_cursor_oldest_first()
    {
        var (svc, jira, _) = Create();
        jira.On("GET", "/rest/api/2/search/jql?jql=" + Uri.EscapeDataString("project = OPS ORDER BY created ASC") + "&maxResults=2&fields=summary",
            """{"issues":[],"nextPageToken":"tok2","isLast":false}""");

        Assert.Equal("No issues.\nnext cursor: tok2", await svc.MigrateAsync("o/r", null, "project = OPS", null, 2, true, Ct));
    }

    [Fact]
    public async Task Rate_limit_stops_the_batch()
    {
        var (svc, jira, gh) = Create();
        JiraSource(jira);
        GitHubTarget(gh);
        gh.On("POST", "/repos/o/r/issues", """{"message":"You have exceeded a secondary rate limit"}""", HttpStatusCode.Forbidden);

        var result = await svc.MigrateAsync("o/r", ["OPS-7", "OPS-8"], null, null, 10, false, Ct);

        Assert.StartsWith("OPS-7 ✗ GitHub API 403: You have exceeded a secondary rate limit\nStopped: rate limited.", result);
        Assert.DoesNotContain(jira.Requests, r => r.PathAndQuery.Contains("OPS-8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitHub_to_Jira_copies_issue_attachments_comments_and_closes()
    {
        var (svc, jira, gh) = Create();
        gh.On("GET", "/repos/o/r/issues/5", """
            {"id":5000,"number":5,"title":"Crash on save","state":"closed","state_reason":"completed","body":"Steps\n\n![screen](https://github.com/user-attachments/assets/abc-123)",
             "user":{"login":"bob"},"labels":[{"name":"bug"},{"name":"good first issue"}],"assignees":[{"login":"jane-gh"}],
             "comments":1,"created_at":"2026-01-02T10:00:00Z","closed_at":"2026-01-09T10:00:00Z","html_url":"https://github.com/o/r/issues/5"}
            """);
        gh.On("GET", "/repos/o/r/issues/5/comments?per_page=100&page=1", """
            [{"id":11,"user":{"login":"amy"},"body":"Thanks!","created_at":"2026-01-03T08:15:00Z","html_url":"https://github.com/o/r/issues/5#issuecomment-11"}]
            """);
        gh.On("GET", "/repos/o/r/issues/5/sub_issues?per_page=100", "[]");
        gh.On("GET", "/user-attachments/assets/abc-123", "PNG", mediaType: "image/png");
        jira.On("GET", "/rest/api/2/user/search?query=Jane%20Doe&maxResults=10", """[{"accountId":"a1","displayName":"Jane Doe","active":true}]""");
        jira.On("POST", "/rest/api/2/issue", """{"id":"1","key":"OPS-9"}""", HttpStatusCode.Created);
        jira.On("POST", "/rest/api/2/issue/OPS-9/attachments", """[{"filename":"abc-123.png","size":3}]""");
        jira.On("POST", "/rest/api/2/issue/OPS-9/comment", "{}", HttpStatusCode.Created);
        jira.On("GET", "/rest/api/2/issue/OPS-9/transitions", """
            {"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress","statusCategory":{"key":"indeterminate"}}},
                            {"id":"31","name":"Close","to":{"name":"Done","statusCategory":{"key":"done"}}}]}
            """);
        jira.On("POST", "/rest/api/2/issue/OPS-9/transitions", "", HttpStatusCode.NoContent);

        var result = await svc.MigrateAsync("ops", ["#5"], null, null, 10, false, Ct);

        Assert.Equal("o/r#5 → OPS-9 | 1 comments, 1 attachments | https://acme.atlassian.net/browse/OPS-9", result);
        var create = jira.Requests.Single(r => r is { Method.Method: "POST", PathAndQuery: "/rest/api/2/issue" }).Body!;
        Assert.Contains("\"issuetype\":{\"name\":\"Bug\"}", create);
        Assert.Contains("\"labels\":[\"bug\",\"good-first-issue\"]", create);
        Assert.Contains("\"assignee\":{\"accountId\":\"a1\"}", create);
        var description = System.Text.Json.JsonDocument.Parse(create).RootElement.GetProperty("fields").GetProperty("description").GetString();
        Assert.Contains("Migrated from GitHub [o/r#5|https://github.com/o/r/issues/5]", description);
        Assert.Contains("!abc-123.png!", description);
        Assert.Contains("Thanks\\!", Json(jira.Requests.Single(r => r.PathAndQuery == "/rest/api/2/issue/OPS-9/comment").Body!, "body")); // wiki-escaped
        Assert.Contains("\"id\":\"31\"", jira.Requests.Single(r => r is { Method.Method: "POST", PathAndQuery: "/rest/api/2/issue/OPS-9/transitions" }).Body);
    }

    [Fact]
    public async Task Resumed_GitHub_to_Jira_issue_skips_attachments_that_are_already_there()
    {
        var (svc, jira, gh) = Create();
        gh.On("GET", "/repos/o/r/issues/5", """
            {"id":5000,"number":5,"title":"Crash on save","state":"open","body":"![screen](https://github.com/user-attachments/assets/abc-123)",
             "user":{"login":"bob"},"comments":0,"html_url":"https://github.com/o/r/issues/5"}
            """);
        gh.On("GET", "/user-attachments/assets/abc-123", "PNG", mediaType: "image/png");
        jira.On("GET", $"/rest/api/2/issue/OPS-9?fields={Fields}", """{"key":"OPS-9","fields":{"attachment":[{"filename":"abc-123.png","size":3}]}}""");
        File.WriteAllText(MapFile, """
            {"issues":{"github:github.com/o/r#5 -> jira:acme.atlassian.net/OPS":{"target":"OPS-9","url":"https://acme.atlassian.net/browse/OPS-9"}}}
            """);

        var result = await svc.MigrateAsync("OPS", ["#5"], null, null, 10, false, Ct);

        Assert.StartsWith("o/r#5 → OPS-9", result);
        Assert.DoesNotContain(jira.Requests, r => r.PathAndQuery.EndsWith("/attachments", StringComparison.Ordinal));
        Assert.DoesNotContain(jira.Requests, r => r is { Method.Method: "POST", PathAndQuery: "/rest/api/2/issue" });
    }

    [Fact]
    public void Map_writers_sharing_a_file_keep_each_others_records()
    {
        var first = MigrationMap.Load(MapFile);
        var second = MigrationMap.Load(MapFile); // e.g. the stdio and the HTTP instance

        first.Set("a -> x", new MigrationRecord { Target = "o/r#1" });
        second.Set("b -> x", new MigrationRecord { Target = "o/r#2" });

        var reloaded = MigrationMap.Load(MapFile);
        Assert.Equal("o/r#1", reloaded.Get("a -> x")?.Target);
        Assert.Equal("o/r#2", reloaded.Get("b -> x")?.Target);
        Assert.Single(Directory.GetFiles(_dir)); // no temp files left behind
    }

    [Fact]
    public void Relative_map_file_is_kept_next_to_the_settings_not_in_the_working_directory()
    {
        var settingsDir = Path.Combine(_dir, "cfg");
        var defaultMap = Path.Combine(settingsDir, "migration-map.json");

        Assert.Equal(defaultMap, MigrationService.MapPath(null, defaultMap));
        Assert.Equal(Path.Combine(settingsDir, "acme", "map.json"), MigrationService.MapPath(Path.Combine("acme", "map.json"), defaultMap));
        var absolute = Path.Combine(_dir, "elsewhere.json");
        Assert.Equal(absolute, MigrationService.MapPath(absolute, defaultMap));
    }

    [Theory]
    [InlineData("https://github.com/user-attachments/assets/abc-123", true)]
    [InlineData("https://github.com/o/r/assets/12345/0a1b2c3d-1111-2222-3333-444455556666", true)]
    [InlineData("https://github.com/o/r/blob/main/assets/logo.png", false)]   // a page in the repository
    [InlineData("https://github.com/o/r/tree/main/docs/assets/x", false)]
    [InlineData("https://user-images.githubusercontent.com/1/2.png", true)]
    public void Only_uploaded_files_count_as_GitHub_attachments(string url, bool attachment) =>
        Assert.Equal(attachment, MigrationText.GitHubAssets($"see {url} here", "github.com").Any());

    [Fact]
    public async Task Pull_requests_are_skipped()
    {
        var (svc, _, gh) = Create();
        gh.On("GET", "/repos/o/r/issues/6", """{"number":6,"title":"PR","state":"open","pull_request":{"url":"x"},"comments":0}""");

        Assert.Equal("o/r#6 skipped: pull request", await svc.MigrateAsync("OPS", ["o/r#6"], null, null, 10, false, Ct));
    }

    private static string Json(string json, string property) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty(property).GetString()!;

    [Theory]
    [InlineData("#12", "o/r", 12)]
    [InlineData("12", "o/r", 12)]
    [InlineData("x/y#3", "x/y", 3)]
    public void Parses_github_refs(string reference, string repo, int number) =>
        Assert.Equal((repo, number), MigrationService.ParseRef(reference, "o/r"));

    [Fact]
    public void User_map_matches_jira_users_both_ways()
    {
        var map = UserMap.Parse("jdoe=octo; jane@acme.com=@jane-gh\n5b10ac8d82e05b22cc7d4ef5=acc");

        Assert.Equal("octo", map.ToGitHub(new JiraUser { Name = "JDOE" }));
        Assert.Equal("jane-gh", map.ToGitHub(new JiraUser { EmailAddress = "jane@acme.com" }));
        Assert.Equal("acc", map.ToGitHub("5b10ac8d82e05b22cc7d4ef5"));
        Assert.Equal("jdoe", map.ToJira("@Octo"));
        Assert.Null(map.ToGitHub(new JiraUser { Name = "other" }));
        Assert.Throws<ArgumentException>(() => UserMap.Parse("nope"));
    }
}
