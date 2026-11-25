using System.Net;
using System.Text.Json;
using DevToolsMcp.Server.Modules.Jira;

namespace DevToolsMcp.Tests.Jira;

public class JiraServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (JiraService Service, JiraFakeHttp Http) Create(string baseUrl = "https://acme.atlassian.net", string? email = "me@acme.com")
    {
        var options = new JiraOptions { BaseUrl = baseUrl, Email = email, ApiToken = "t" };
        var http = new JiraFakeHttp();
        return (new JiraService(JiraModule.CreateAdapter(options.ResolveDeploymentType(), http, options)), http);
    }

    private const string SearchFields = "summary,status,issuetype,assignee,updated";

    private const string IssueJson = """
        {"id":"10001","key":"OPS-7","self":"x","fields":{"summary":"Broken build","description":"h2. Steps\n* run *make*\n* see {{error}}",
         "status":{"name":"In Progress","iconUrl":"x"},"issuetype":{"name":"Bug"},"priority":{"name":"High"},
         "assignee":{"accountId":"a1","displayName":"Jane Doe","avatarUrls":{"48x48":"x"}},"reporter":{"displayName":"John"},
         "labels":["ci","urgent"],"parent":{"key":"OPS-1","fields":{"summary":"Epic"}},
         "attachment":[{"id":"9","filename":"log.txt","size":2048}],
         "created":"2026-11-01T10:00:00.000+0000","updated":"2026-11-25T08:00:00.000+0000","customfield_10000":"noise"}}
        """;

    [Fact]
    public async Task Cloud_search_uses_enhanced_jql_endpoint_and_one_line_per_issue()
    {
        var (jira, http) = Create();
        http.On("GET", $"/rest/api/2/search/jql?jql=project%20%3D%20OPS&maxResults=2&fields={SearchFields}", """
            {"issues":[{"key":"OPS-7","fields":{"summary":"Broken build","status":{"name":"Open"},"issuetype":{"name":"Bug"},"updated":"2026-11-25T08:00:00.000+0000"}},
                       {"key":"OPS-8","fields":{"summary":"Docs","status":{"name":"Done"},"issuetype":{"name":"Task"},"assignee":{"displayName":"Jane"}}}],
             "nextPageToken":"abc","isLast":false}
            """);

        var text = await jira.SearchAsync("project = OPS", 2, Ct);

        Assert.Equal("OPS-7 | Bug | Open | Unassigned | 2026-11-25 | Broken build\nOPS-8 | Task | Done | Jane | - | Docs\n(more results; refine the JQL)", text);
    }

    [Fact]
    public async Task Data_center_search_uses_context_path_and_reports_total()
    {
        var (jira, http) = Create("https://jira.corp/jira", null);
        http.On("GET", $"/jira/rest/api/2/search?jql=key%20%3D%20OPS-7&maxResults=20&fields={SearchFields}",
            """{"issues":[{"key":"OPS-7","fields":{"summary":"S","status":{"name":"Open"},"issuetype":{"name":"Bug"}}}],"total":1}""");

        Assert.Equal("OPS-7 | Bug | Open | Unassigned | - | S", await jira.SearchAsync("ops-7", 20, Ct));
        Assert.Equal("Bearer", http.Requests[0].Headers["Authorization"].Split(' ')[0]);
    }

    [Theory]
    [InlineData("login fails", "text ~ \"login fails\" ORDER BY updated DESC")]
    [InlineData("say \"hi\"", "text ~ \"say \\\"hi\\\"\" ORDER BY updated DESC")]
    [InlineData("assignee = currentUser() ORDER BY priority", "assignee = currentUser() ORDER BY priority")]
    [InlineData("labels is EMPTY", "labels is EMPTY")]
    [InlineData("status not in (Done)", "status not in (Done)")]
    [InlineData("abc-12", "key = ABC-12")]
    public void Plain_text_becomes_text_search(string query, string expected) => Assert.Equal(expected, JiraJql.Build(query));

    [Fact]
    public async Task Get_issue_returns_front_matter_markdown_and_latest_comments_oldest_first()
    {
        var (jira, http) = Create();
        http.On("GET", "/rest/api/2/issue/OPS-7?fields=summary,description,status,issuetype,priority,resolution,assignee,reporter,labels,parent,attachment,created,updated", IssueJson)
            .On("GET", "/rest/api/2/issue/OPS-7/comment?orderBy=-created&maxResults=2", """
                {"startAt":0,"maxResults":2,"total":3,"comments":[
                  {"id":"3","author":{"displayName":"Jane Doe"},"body":"Fixed in _main_","created":"2026-11-24T09:00:00.000+0000"},
                  {"id":"2","author":{"displayName":"John"},"body":"Still *broken*","created":"2026-11-23T09:00:00.000+0000"}]}
                """);

        var text = await jira.GetIssueAsync("ops-7", 2, Ct);

        Assert.Equal("""
            ---
            key: OPS-7
            summary: Broken build
            type: Bug
            status: In Progress
            priority: High
            assignee: Jane Doe
            reporter: John
            labels: ci, urgent
            parent: OPS-1 Epic
            created: 2026-11-01
            updated: 2026-11-25
            url: https://acme.atlassian.net/browse/OPS-7
            attachments: log.txt (2 KB)
            ---
            ## Steps
            - run **make**
            - see `error`

            ## Comments (last 2 of 3)

            **John** 2026-11-23:
            Still **broken**

            **Jane Doe** 2026-11-24:
            Fixed in *main*
            """.ReplaceLineEndings("\n"), text);
        Assert.DoesNotContain("noise", text);
    }

    [Fact]
    public async Task Get_issue_without_comments_skips_the_comment_call()
    {
        var (jira, http) = Create();
        http.On("GET", "/rest/api/2/issue/OPS-7?fields=summary,description,status,issuetype,priority,resolution,assignee,reporter,labels,parent,attachment,created,updated", IssueJson);
        await jira.GetIssueAsync("OPS-7", 0, Ct);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Create_converts_markdown_and_assigns_by_account_id_on_cloud()
    {
        var (jira, http) = Create();
        http.On("GET", "/rest/api/2/myself", """{"accountId":"me-1","displayName":"Me"}""")
            .On("POST", "/rest/api/2/issue", """{"id":"10002","key":"OPS-9","self":"x"}""", HttpStatusCode.Created);

        var text = await jira.CreateIssueAsync("ops", "New thing", null, "Do **this**", "me", ["a", "b"], "Low", "OPS-1", Ct);

        Assert.Equal("Created OPS-9 | https://acme.atlassian.net/browse/OPS-9", text);
        var fields = JsonDocument.Parse(http.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement.GetProperty("fields");
        Assert.Equal("OPS", fields.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal("Task", fields.GetProperty("issuetype").GetProperty("name").GetString());
        Assert.Equal("Do *this*", fields.GetProperty("description").GetString());
        Assert.Equal("me-1", fields.GetProperty("assignee").GetProperty("accountId").GetString());
        Assert.False(fields.GetProperty("assignee").TryGetProperty("name", out _));
        Assert.Equal("OPS-1", fields.GetProperty("parent").GetProperty("key").GetString());
        Assert.Equal(2, fields.GetProperty("labels").GetArrayLength());
    }

    [Fact]
    public async Task Create_with_unknown_issue_type_lists_valid_types()
    {
        var (jira, http) = Create();
        http.On("POST", "/rest/api/2/issue", """{"errorMessages":[],"errors":{"issuetype":"Specify a valid issue type"}}""", HttpStatusCode.BadRequest)
            .On("GET", "/rest/api/2/project/OPS", """{"key":"OPS","issueTypes":[{"name":"Bug"},{"name":"Story"}]}""");

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => jira.CreateIssueAsync("OPS", "x", "Feature", null, null, null, null, null, Ct));
        Assert.Equal("Jira API 400: issuetype: Specify a valid issue type Valid issue types for OPS: Bug, Story.", ex.Message);
    }

    [Fact]
    public async Task Update_sends_only_given_fields_label_operations_and_unassign()
    {
        var (jira, http) = Create();
        http.On("PUT", "/rest/api/2/issue/OPS-7", "", HttpStatusCode.NoContent)
            .On("PUT", "/rest/api/2/issue/OPS-7/assignee", "", HttpStatusCode.NoContent);

        var text = await jira.UpdateIssueAsync("OPS-7", "Renamed", null, null, ["new"], ["old"], "none", Ct);

        Assert.Equal("Updated OPS-7: summary, labels, assignee → Unassigned.", text);
        var edit = JsonDocument.Parse(http.Requests[0].Body!).RootElement;
        Assert.Equal("""{"fields":{"summary":"Renamed"},"update":{"labels":[{"add":"new"},{"remove":"old"}]}}""", edit.GetRawText());
        Assert.Equal("""{"accountId":null}""", http.Requests[1].Body);
    }

    [Fact]
    public async Task Data_center_assign_resolves_user_by_name()
    {
        var (jira, http) = Create("https://jira.corp", null);
        http.On("GET", "/rest/api/2/user/search?username=jane&maxResults=10", """
                [{"name":"jane","displayName":"Jane Doe","emailAddress":"jane@corp","active":true},
                 {"name":"janet","displayName":"Janet","active":true}]
                """)
            .On("PUT", "/rest/api/2/issue/OPS-7/assignee", "", HttpStatusCode.NoContent);

        Assert.Equal("Updated OPS-7: assignee → Jane Doe.", await jira.UpdateIssueAsync("OPS-7", null, null, null, null, null, "jane", Ct));
        Assert.Equal("""{"name":"jane"}""", http.Requests[^1].Body);
    }

    [Fact]
    public async Task Ambiguous_user_is_reported_with_candidates()
    {
        var (jira, http) = Create();
        http.On("GET", "/rest/api/2/user/search?query=jo&maxResults=10", """
            [{"accountId":"1","displayName":"John","emailAddress":"john@a"},{"accountId":"2","displayName":"Joan"},{"accountId":"3","displayName":"Gone","active":false}]
            """);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => jira.UpdateIssueAsync("OPS-7", null, null, null, null, null, "jo", Ct));
        Assert.Equal("'jo' matches several users, be more specific: John <john@a>, Joan (2)", ex.Message);
        Assert.Single(http.Requests); // nothing was written
    }

    [Fact]
    public async Task Update_without_changes_is_rejected()
    {
        var (jira, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => jira.UpdateIssueAsync("OPS-7", null, null, null, [], null, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => jira.UpdateIssueAsync("OPS-7", null, null, null, ["two words"], null, null, Ct));
    }

    private const string Transitions = """
        {"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}},{"id":"31","name":"Close","to":{"name":"Done"}}]}
        """;

    [Fact]
    public async Task Transition_without_name_lists_transitions()
    {
        var (jira, http) = Create();
        http.On("GET", "/rest/api/2/issue/OPS-7/transitions", Transitions);
        Assert.Equal("Transitions for OPS-7 (id | name → status):\n11 | Start → In Progress\n31 | Close → Done",
            await jira.TransitionAsync("OPS-7", null, null, null, Ct));
    }

    [Fact]
    public async Task Transition_matches_target_status_and_adds_comment()
    {
        var (jira, http) = Create();
        http.On("GET", "/rest/api/2/issue/OPS-7/transitions", Transitions)
            .On("POST", "/rest/api/2/issue/OPS-7/transitions", "", HttpStatusCode.NoContent)
            .On("POST", "/rest/api/2/issue/OPS-7/comment", """{"id":"5"}""", HttpStatusCode.Created);

        Assert.Equal("OPS-7 → Done. Comment added.", await jira.TransitionAsync("OPS-7", "done", "Shipped in `1.2`", "Fixed", Ct));
        Assert.Equal("""{"transition":{"id":"31"},"fields":{"resolution":{"name":"Fixed"}}}""", http.Requests[1].Body);
        Assert.Equal("""{"body":"Shipped in {{1.2}}"}""", http.Requests[2].Body);
    }

    [Fact]
    public async Task Unknown_transition_lists_the_available_ones()
    {
        var (jira, http) = Create();
        http.On("GET", "/rest/api/2/issue/OPS-7/transitions", Transitions);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => jira.TransitionAsync("OPS-7", "Reopen", null, null, Ct));
        Assert.Contains("31 | Close → Done", ex.Message);
    }

    [Fact]
    public async Task Attach_uploads_multipart_with_xsrf_header()
    {
        var (jira, http) = Create();
        http.On("POST", "/rest/api/2/issue/OPS-7/attachments", """[{"id":"9","filename":"report.txt","size":5}]""");
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "attach-" + Guid.NewGuid().ToString("N")));
        try
        {
            var file = Path.Combine(dir.FullName, "report.txt");
            await File.WriteAllTextAsync(file, "hello", Ct);

            Assert.Equal("Attached report.txt (5 B) to OPS-7.", await jira.AttachFileAsync("OPS-7", file, Ct));
            var request = http.Requests.Single();
            Assert.Equal("no-check", request.Headers["X-Atlassian-Token"]);
            Assert.Equal("multipart/form-data", request.ContentType);
            Assert.Contains("name=\"file\"; filename=\"report.txt\"", request.Body);
            Assert.Contains("hello", request.Body);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Attach_refuses_hidden_folders_and_missing_files()
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, ".secrets-" + Guid.NewGuid().ToString("N")));
        try
        {
            var file = Path.Combine(dir.FullName, "id_rsa");
            File.WriteAllText(file, "key");
            Assert.Contains("hidden", Assert.Throws<ArgumentException>(() => JiraService.ValidateAttachmentPath(file)).Message);
            Assert.Contains("not found", Assert.Throws<ArgumentException>(() => JiraService.ValidateAttachmentPath(file + ".missing")).Message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Errors_are_flattened()
    {
        var (jira, http) = Create();
        http.On("POST", "/rest/api/2/issue/OPS-7/comment", """{"errorMessages":["You do not have permission"],"errors":{}}""", HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<JiraApiException>(() => jira.AddCommentAsync("OPS-7", "x", Ct));
        Assert.Equal("Jira API 403: You do not have permission", ex.Message);
    }

    [Theory]
    [InlineData("OPS 7")]
    [InlineData("../OPS-7")]
    [InlineData("")]
    public async Task Invalid_keys_are_rejected(string key)
    {
        var (jira, http) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => jira.GetIssueAsync(key, 0, Ct));
        Assert.Empty(http.Requests);
    }
}
