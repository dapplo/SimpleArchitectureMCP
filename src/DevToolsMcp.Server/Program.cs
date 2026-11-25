using DevToolsMcp.Server.Core.Json;
using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Core.Security;
using DevToolsMcp.Server.Core.Settings;
using DevToolsMcp.Server.Core.Telemetry;
using DevToolsMcp.Server.Modules.Confluence;
using DevToolsMcp.Server.Modules.Jira;

var builder = WebApplication.CreateSlimBuilder(args);

var settingsFile = SettingsStore.AddTo(builder.Configuration); // per-user settings written by the settings page
var port = builder.ConfigureLocalKestrel();                     // 127.0.0.1 / ::1 only, port 5180 by default
builder.AddDevToolsTelemetry();                                  // OTLP traces/metrics/logs (Aspire dashboard)

// 1) Compose the AOT-safe JSON contexts of all modules.
var json = DevToolsJson.Compose(ConfluenceJsonContext.Default, JiraJsonContext.Default);
builder.Services.ConfigureHttpJsonOptions(o => json.ApplyTo(o.SerializerOptions));

// 2) MCP server over Streamable HTTP. Stateless: tools only, no server-initiated requests, nothing to clean up.
var mcp = builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = DevToolsTelemetry.ServiceName, Version = "1.0.0" };
        o.Capabilities = new() { Tools = new() }; // answer tools/list (empty) even when no module is configured
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithModuleToolGate();                      // hide tools of unconfigured modules (zero schema tokens)

// 3) Register modules.
mcp.AddConfluenceModule(builder.Configuration, json.McpOptions);
mcp.AddJiraModule(builder.Configuration, json.McpOptions);

var app = builder.Build();

var registry = app.Services.GetRequiredService<ModuleRegistry>();
var baseUrl = $"http://localhost:{port}/";
var store = new SettingsStore(settingsFile, (IConfigurationRoot)app.Configuration);

app.UseOptionalApiKey();
app.MapGet("/health", (IServiceProvider sp) => new HealthResponse("ok", registry.ActiveModules(sp).ToList()));
app.MapSettings(store, baseUrl + "mcp");
app.MapMcp("/mcp");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DevToolsMcp");
    foreach (var m in registry.Modules)
    {
        var status = m.Status(app.Services);
        log.LogInformation("{Module}: {State} ({Status})", m.Title, status.Active ? "active" : "inactive", status.Message);
    }

    log.LogInformation("MCP endpoint: {Mcp}  Settings: {Settings}", baseUrl + "mcp", baseUrl);

    // First run (nothing configured): open the settings page. Disable with DevToolsMcp:OpenBrowser=false.
    var openBrowser = !string.Equals(app.Configuration["DevToolsMcp:OpenBrowser"], "false", StringComparison.OrdinalIgnoreCase);
    if (openBrowser && !registry.ActiveModules(app.Services).Any())
    {
        BrowserLauncher.TryOpen(baseUrl, log);
    }
});

app.Run();
