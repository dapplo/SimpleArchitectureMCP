using System.Net;
using System.Text.Json;
using DevToolsMcp.Server.Core.Tools;
using DevToolsMcp.Server.Modules.Jira;
using ModelContextProtocol;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace DevToolsMcp.Tests.Core;

public class ToolErrorsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Module_and_input_errors_keep_their_message()
    {
        Assert.Equal("Jira API 404: not found", ToolErrors.Describe(new JiraApiException(HttpStatusCode.NotFound, "Jira API 404: not found"), "Jira", Ct));
        Assert.Equal("Jira is not configured (BaseUrl is empty). Open the devtools-mcp settings page.",
            ToolErrors.Describe(new JiraNotConfiguredException("BaseUrl is empty"), "Jira", Ct));
        Assert.Equal("query is required.", ToolErrors.Describe(new ArgumentException("query is required."), "Jira", Ct));
    }

    [Fact]
    public void Timeouts_and_outages_become_actionable_messages()
    {
        Assert.StartsWith("Jira did not answer in time", ToolErrors.Describe(new TimeoutRejectedException(), "Jira", Ct));
        Assert.StartsWith("Jira did not answer in time", ToolErrors.Describe(new TaskCanceledException(), "Jira", Ct));
        Assert.StartsWith("GitHub failed several times", ToolErrors.Describe(new BrokenCircuitException(), "GitHub", Ct));
        Assert.StartsWith("Confluence sent an unexpected response", ToolErrors.Describe(new JsonException("'<' is an invalid start of a value."), "Confluence", Ct));
    }

    [Fact]
    public void Http_errors_show_the_useful_inner_message()
    {
        var ex = new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new IOException("The remote certificate is invalid."));
        Assert.Equal("Jira request failed: The remote certificate is invalid.", ToolErrors.Describe(ex, "Jira", Ct));
    }

    [Fact]
    public void Cancelled_calls_and_bugs_are_not_turned_into_tool_errors()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(ToolErrors.Describe(new OperationCanceledException(cts.Token), "Jira", cts.Token));
        Assert.Null(ToolErrors.Describe(new NullReferenceException(), "Jira", Ct));
        Assert.Null(ToolErrors.Describe(new McpException("already mapped"), "Jira", Ct));
    }

    [Fact]
    public async Task RunAsync_throws_McpException_with_the_message()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            ToolErrors.RunAsync("jira_search", "Jira", Task<string> () => throw new TimeoutRejectedException(), Ct));
        Assert.StartsWith("Jira did not answer in time", ex.Message);

        Assert.Equal("ok", await ToolErrors.RunAsync("jira_search", "Jira", () => Task.FromResult("ok"), Ct));
    }
}
