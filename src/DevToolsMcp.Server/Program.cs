using DevToolsMcp.Server.Core.Json;
using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Core.Security;
using DevToolsMcp.Server.Core.Telemetry;
using DevToolsMcp.Server.Modules.Confluence;

var builder = WebApplication.CreateSlimBuilder(args);

builder.ConfigureLocalKestrel();   // 127.0.0.1 / ::1 only, port 5180 by default
builder.AddDevToolsTelemetry();    // OTLP traces/metrics/logs (Aspire dashboard)

// 1) Compose the AOT-safe JSON contexts of all modules (add JiraJsonContext.Default, GitHubJsonContext.Default, ...).
var json = DevToolsJson.Compose(ConfluenceJsonContext.Default);
builder.Services.ConfigureHttpJsonOptions(o => json.ApplyTo(o.SerializerOptions));

// 2) MCP server over Streamable HTTP. Stateless: tools only, no server-initiated requests, nothing to clean up.
var mcp = builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = DevToolsTelemetry.ServiceName, Version = "1.0.0" };
        o.Capabilities = new() { Tools = new() }; // answer tools/list (empty) even when no module is configured
    })
    .WithHttpTransport(o => o.Stateless = true);

// 3) Modules register their tools only when configured (zero schema tokens otherwise).
mcp.AddConfluenceModule(builder.Configuration, json.McpOptions);
// mcp.AddJiraModule(builder.Configuration, json.McpOptions);

var registry = ModuleRegistry.From(builder.Services);

var app = builder.Build();

app.UseOptionalApiKey();
app.MapGet("/health", () => new HealthResponse("ok", registry.Enabled));
app.MapMcp("/mcp");

var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DevToolsMcp");
log.LogInformation("Modules enabled: [{Enabled}] disabled: [{Disabled}]",
    string.Join(", ", registry.Enabled), string.Join(", ", registry.Disabled));

app.Run();
