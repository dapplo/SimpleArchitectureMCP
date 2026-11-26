using System.Text.Json;
using SimpleArchitectureMcp.Server.Core.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace SimpleArchitectureMcp.Server.Core.Tools;

/// <summary>Marks an exception whose message is written for the model: short, actionable, no secrets.</summary>
public interface IToolError;

/// <summary>
/// One error policy for every tool. Expected failures (bad input, API errors, timeouts, unreachable hosts, unexpected
/// responses) become a short tool error the model can act on. Anything else is left to the MCP SDK, which logs it and
/// returns a generic error, so stack traces and internals never reach the model. A cancelled call stays cancelled.
/// </summary>
public static class ToolErrors
{
    /// <summary>Runs a text tool: records the output size and maps expected failures.</summary>
    /// <param name="service">The remote side, for messages such as "Jira did not answer in time".</param>
    public static async Task<string> RunAsync(string tool, string service, Func<Task<string>> action, CancellationToken ct)
    {
        try
        {
            return DevToolsTelemetry.Output(tool, await action().ConfigureAwait(false));
        }
        catch (Exception ex) when (Describe(ex, service, ct) is { } message)
        {
            throw new McpException(message, ex);
        }
    }

    /// <summary>Runs a tool with a rich result (e.g. images); the first content block is the text.</summary>
    public static async Task<CallToolResult> RunAsync(string tool, string service, Func<Task<CallToolResult>> action,
        CancellationToken ct)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            DevToolsTelemetry.Output(tool, result.Content is [TextContentBlock text, ..] ? text.Text : string.Empty);
            return result;
        }
        catch (Exception ex) when (Describe(ex, service, ct) is { } message)
        {
            throw new McpException(message, ex);
        }
    }

    /// <summary>
    /// The message for an expected failure, or null when <paramref name="ex"/> is a bug or a cancellation by the caller.
    /// </summary>
    /// <param name="ct">The call's token: a cancellation it did not ask for is a timeout.</param>
    public static string? Describe(Exception ex, string service, CancellationToken ct) => ex switch
    {
        McpException => null,                                    // already a tool error
        IToolError or ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException
            or System.Xml.XmlException => ex.Message,
        TimeoutRejectedException => $"{service} did not answer in time. Try again, ask for less, or raise TimeoutSeconds.",
        BrokenCircuitException => $"{service} failed several times in a row, so calls pause for a few seconds. Try again shortly.",
        HttpRequestException http => $"{service} request failed: {Innermost(http)}",
        JsonException => $"{service} sent an unexpected response (not the expected JSON). Check the base URL: a login page, VPN or proxy may be answering instead.",
        // HttpClient and our own deadlines surface as cancellations; a call the client cancelled stays cancelled.
        OperationCanceledException when !ct.IsCancellationRequested => $"{service} did not answer in time. Try again, ask for less, or raise TimeoutSeconds.",
        _ => null,
    };

    /// <summary>"The SSL connection could not be established, see inner exception." is no help; the inner message is.</summary>
    private static string Innermost(HttpRequestException ex)
    {
        Exception e = ex;
        while (e.InnerException is { } inner && e.Message.Contains("inner exception", StringComparison.OrdinalIgnoreCase))
        {
            e = inner;
        }

        return e.Message;
    }
}
