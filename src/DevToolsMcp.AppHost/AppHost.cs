using System.Runtime.InteropServices;

var builder = DistributedApplication.CreateBuilder(args);

// Integration parameters. Every provider follows the same <provider>-url / -email / -token pattern, mapped to
// <Provider>__BaseUrl / __Email / __ApiToken. Add a provider by adding one more Provider(...) line.
Provider[] providers =
[
    Provider.Create(builder, "confluence", "Confluence", withDeploymentType: true),
    Provider.Create(builder, "jira", "Jira", withDeploymentType: true),
    // Provider.Create(builder, "github", "GitHub"),
];

// Prefer the published Native AOT single-file binary (sub-50 ms start); fall back to the project during development.
// Publish it with:  dotnet publish src/DevToolsMcp.Server -c Release -r <rid> -o artifacts/publish/<rid>
var binary = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "artifacts", "publish",
    RuntimeInformation.RuntimeIdentifier, OperatingSystem.IsWindows() ? "devtools-mcp.exe" : "devtools-mcp"));

if (File.Exists(binary))
{
    builder.AddExecutable("devtools-mcp", binary, Path.GetDirectoryName(binary)!)
        .WithOtlpExporter()
        .ConfigureMcpServer(providers);
}
else
{
    builder.AddDotnetProject("devtools-mcp", Path.Combine("..", "DevToolsMcp.Server", "DevToolsMcp.Server.csproj"))
        .ConfigureMcpServer(providers);
}

builder.Build().Run();

internal sealed record Provider(
    string Section,
    IResourceBuilder<ParameterResource> Url,
    IResourceBuilder<ParameterResource> Email,
    IResourceBuilder<ParameterResource> Token,
    IResourceBuilder<ParameterResource>? DeploymentType)
{
    public static Provider Create(IDistributedApplicationBuilder builder, string prefix, string section, bool withDeploymentType = false) => new(
        section,
        builder.AddParameter($"{prefix}-url").WithDescription($"{section} base URL"),
        builder.AddParameter($"{prefix}-email").AsOptional().WithDescription($"{section} account email (Cloud only)"),
        builder.AddParameter($"{prefix}-token", secret: true).WithDescription($"{section} API token / personal access token"),
        withDeploymentType ? builder.AddParameter($"{prefix}-deployment", "Auto").WithDescription("Auto, Cloud or DataCenter") : null);
}

internal static class McpServerResourceExtensions
{
    public static IResourceBuilder<T> ConfigureMcpServer<T>(this IResourceBuilder<T> resource, IEnumerable<Provider> providers)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        // Aspire's proxy listens on localhost:5180 and forwards to the target port it passes in PORT.
        resource
            .WithHttpEndpoint(port: McpPort, env: "PORT", name: "http")
            .WithMcpServer(path: "/mcp", endpointName: "http")
            .WithHttpHealthCheck("/health")
            .WithUrlForEndpoint("http", u => u.DisplayText = "Settings")
            .WithEnvironment("DevToolsMcp__OpenBrowser", "false"); // the dashboard links to the settings page instead

        foreach (var p in providers)
        {
            resource
                .WithEnvironment($"{p.Section}__BaseUrl", p.Url)
                .WithEnvironment($"{p.Section}__Email", p.Email)
                .WithEnvironment($"{p.Section}__ApiToken", p.Token);
            if (p.DeploymentType is not null)
            {
                resource.WithEnvironment($"{p.Section}__DeploymentType", p.DeploymentType);
            }
        }

        return resource;
    }

    private const int McpPort = 5180;
}
