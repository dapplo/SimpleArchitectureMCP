using DevToolsMcp.Server.Core.Json;
using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Core.Security;
using DevToolsMcp.Server.Core.Settings;
using DevToolsMcp.Server.Core.Telemetry;
using DevToolsMcp.Server.Modules.Archi;
using DevToolsMcp.Server.Modules.Confluence;
using DevToolsMcp.Server.Modules.Jira;
using DevToolsMcp.Server.Modules.GitHub;
using DevToolsMcp.Server.Modules.Migration;

// --stdio: the MCP client starts this process and talks MCP over stdin/stdout (no Node.js bridge needed).
// The settings page and /health stay on HTTP; stdout carries only protocol messages, logs go to stderr.
var stdio = args.Any(a => string.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase));
args = [.. args.Where(a => !string.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase))];
args = ToolScopeArgs.Expand(args);   // --tools jira,github  ->  --DevToolsMcp:Tools=jira,github

var builder = WebApplication.CreateSlimBuilder(args);
if (stdio)
{
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
}

// Per-user settings written by the settings page. A stdio instance watches the file, because the page it is edited
// on may belong to another devtools-mcp instance.
var settingsFile = SettingsStore.AddTo(builder.Configuration, watch: stdio);
if (LocalHosting.StartupProblem(builder.Configuration) is { } problem)
{
    Console.Error.WriteLine(problem);
    return 1;
}

var port = LocalHosting.ResolvePort(builder.Configuration);   // 127.0.0.1 / ::1 only, port 5180 by default
var ownsPort = PortGuard.IsFree(port, LocalHosting.AllowRemote(builder.Configuration));

// Started a second time (e.g. double-clicked again)? Point at the running instance instead of crashing on bind.
// A stdio instance keeps running: its MCP traffic doesn't need the port, and the running instance hosts the page.
if (!ownsPort && !stdio)
{
    return await PortGuard.ReportInUseAsync(port, builder.Configuration);
}

builder.ConfigureLocalKestrel(ownsPort ? port : 0);
builder.AddDevToolsTelemetry();                                  // OTLP traces/metrics/logs (Aspire dashboard)

// 1) Compose the AOT-safe JSON contexts of all modules.
var json = DevToolsJson.Compose(ConfluenceJsonContext.Default, JiraJsonContext.Default, GitHubJsonContext.Default,
    ArchiJsonContext.Default);
builder.Services.ConfigureHttpJsonOptions(o => json.ApplyTo(o.SerializerOptions));

// 2) MCP server over Streamable HTTP. Stateless: tools only, no server-initiated requests, nothing to clean up.
var mcp = builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = DevToolsTelemetry.ServiceName, Version = "1.0.0" };
        // Answer tools/list (empty) even when no module is configured. A stdio session lives as long as the client,
        // so it announces tool list changes after a settings change; stateless HTTP has no session to notify.
        o.Capabilities = new() { Tools = new() { ListChanged = stdio } };
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithModuleToolGate();                      // hide tools of unconfigured modules (zero schema tokens)
if (stdio)
{
    mcp.WithStdioServerTransport();             // ends the process when the client closes stdin
}

// 3) Register modules.
mcp.AddConfluenceModule(builder.Configuration, json.McpOptions);
mcp.AddJiraModule(builder.Configuration, json.McpOptions);
mcp.AddGitHubModule(builder.Configuration, json.McpOptions);
mcp.AddArchiModule(builder.Configuration, json.McpOptions);
mcp.AddMigrationModule(builder.Configuration, json.McpOptions); // after Jira and GitHub: uses both

var app = builder.Build();

var registry = app.Services.GetRequiredService<ModuleRegistry>();
var baseUrl = $"http://localhost:{port}/";
var store = new SettingsStore(settingsFile, (IConfigurationRoot)app.Configuration);

app.UseMcpOriginGuard();   // DNS rebinding / browser pages must not reach /mcp
app.UseOptionalApiKey();
app.MapGet("/health", (IServiceProvider sp) => new HealthResponse("ok", registry.ActiveModules(sp).ToList()));
app.MapSettings(store, baseUrl + "mcp");
app.MapMcp("/mcp");
app.MapMcp("/mcp/{" + ToolScope.RouteKey + "}");   // e.g. /mcp/jira,github: only those tools (see ToolScope)

app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DevToolsMcp");
    foreach (var m in registry.Modules)
    {
        var status = m.Status(app.Services);
        log.LogInformation("{Module}: {State} ({Status})", m.Title, status.Active ? "active" : "inactive", status.Message);
    }

    log.LogInformation(stdio ? "MCP over stdio  Settings: {Settings}" : "MCP endpoint: {Mcp}  Settings: {Settings}",
        stdio ? baseUrl : baseUrl + "mcp", baseUrl);
    if (!ownsPort)
    {
        log.LogInformation("Port {Port} is in use (usually by another devtools-mcp instance); edit settings on that instance's page.", port);
    }

    if (stdio)
    {
        StdioToolListNotifier.Start(app.Services, app.Configuration, registry, log);
    }

    // First run (nothing configured): open the settings page. Disable with DevToolsMcp:OpenBrowser=false.
    var openBrowser = !string.Equals(app.Configuration["DevToolsMcp:OpenBrowser"], "false", StringComparison.OrdinalIgnoreCase);
    if (openBrowser && ownsPort && !registry.ActiveModules(app.Services).Any())
    {
        BrowserLauncher.TryOpen(baseUrl, log);
    }
});

await app.RunAsync();
return 0;
