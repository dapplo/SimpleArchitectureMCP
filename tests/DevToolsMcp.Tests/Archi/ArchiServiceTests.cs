using System.IO.Compression;
using System.Text.Json;
using DevToolsMcp.Server.Core.Json;
using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Modules.Archi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Tests.Archi;

public sealed class ArchiServiceTests : IDisposable
{
    // 2x3 transparent PNG
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAAEUlEQVR4nGNgYGD4z8DAwMAAAA0AAWnHkHkAAAAASUVORK5CYII=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "archi-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public ArchiServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static (ServiceProvider Provider, ModuleRegistry Registry) Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithModuleToolGate().AddArchiModule(config, DevToolsJson.Compose(ArchiJsonContext.Default).McpOptions);
        return (services.BuildServiceProvider(), ModuleRegistry.From(services));
    }

    /// <summary>
    /// A stand-in for Archi's command line: --saveModel writes a model with two views, --html.createReport writes their
    /// PNGs where Archi puts them (report/&lt;model id&gt;/images/&lt;view id&gt;.png), --help prints the provider options.
    /// </summary>
    private string FakeArchi(bool withJArchi = false)
    {
        var path = Path.Combine(_dir, "Archi");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            while [ $# -gt 0 ]; do
              case "$1" in
                --help) echo "    --loadModel <path>"; {{(withJArchi ? "echo \"    --script.runScript <file>\";" : "")}} ;;
                --xmlexchange.import|--loadModel) [ -f "$2" ] || { echo "missing $2"; exit 1; }; shift ;;
                --saveModel) cat > "$2" <<'XML'
            <?xml version="1.0" encoding="UTF-8"?>
            <archimate:model xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:archimate="http://www.archimatetool.com/archimate" name="M" id="id-m">
              <folder name="Views" type="diagrams">
                <element xsi:type="archimate:ArchimateDiagramModel" name="Main / overview" id="id-v1"/>
                <element xsi:type="archimate:SketchModel" name="Sketch" id="id-v2"/>
              </folder>
            </archimate:model>
            XML
                  shift ;;
                --html.createReport) mkdir -p "$2/id-m/images"; for v in id-v1 id-v2; do echo "{{Png}}" | base64 -d > "$2/id-m/images/$v.png"; done; shift ;;
              esac
              shift
            done
            exit 0
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static void RequireDisplay()
    {
        // The fake doesn't need X; keep the runner from wrapping it in xvfb-run on headless CI.
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Environment.SetEnvironmentVariable("DISPLAY", ":99");
        }
    }

    [Fact]
    public void Registers_two_prefixed_tools_with_small_schemas()
    {
        var (sp, _) = Build([]);
        var tools = sp.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
        Assert.Equal(["archi_create_model", "archi_render"], tools.Select(t => t.Name).Order().ToArray());
        var total = tools.Sum(t => JsonSerializer.Serialize(t.InputSchema).Length + t.Name.Length + (t.Description?.Length ?? 0));
        Assert.True(total < 4500, $"Archi tool schemas are {total} chars");
        var model = tools.Single(t => t.Name == "archi_create_model").InputSchema.GetProperty("properties").GetProperty("model");
        Assert.True(model.GetProperty("properties").TryGetProperty("elements", out _));
    }

    [Fact]
    public void Inactive_until_the_executable_exists()
    {
        var (sp, registry) = Build(new() { ["Archi:ExecutablePath"] = Path.Combine(_dir, "nope") });
        Assert.Empty(registry.ActiveModules(sp));
        Assert.False(registry.IsToolActive("archi_render", sp));
        Assert.StartsWith("Archi not found", sp.GetRequiredService<ArchiService>().Current.Message);
    }

    [Fact]
    public void Folder_or_app_bundle_resolves_to_the_launcher()
    {
        var app = Path.Combine(_dir, "Archi.app", "Contents", "MacOS");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Archi"), "");
        Assert.Equal(Path.Combine(app, "Archi"), ArchiPaths.ResolveExecutable(Path.Combine(_dir, "Archi.app")));
    }

    [Fact]
    public async Task Renders_exchange_file_via_html_report()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        RequireDisplay();
        var output = Path.Combine(_dir, "out");
        var (sp, registry) = Build(new() { ["Archi:ExecutablePath"] = FakeArchi(), ["Archi:OutputDirectory"] = output });
        Assert.Equal(["archi"], registry.ActiveModules(sp));
        var archi = sp.GetRequiredService<ArchiService>();

        var (xml, summary) = await archi.CreateModelAsync(ExchangeModelBuilderTests.Sample(), null, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(output, "Shop.xml"), xml);
        Assert.StartsWith("5 elements, 4 relationships", summary);

        var all = await archi.RenderAsync(xml, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(["Main _ overview", "Sketch"], all.Select(v => Path.GetFileNameWithoutExtension(v.Path)).ToArray());
        Assert.Equal((2, 3), (all[0].Width, all[0].Height));

        var one = await archi.RenderAsync(xml, ["sketch"], Path.Combine(_dir, "pick"), TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(_dir, "pick", "Sketch.png"), Assert.Single(one).Path);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => archi.RenderAsync(xml, ["Other"], null, TestContext.Current.CancellationToken));
        Assert.Contains("Views: Main / overview, Sketch", ex.Message);
        Assert.Equal("Archi command line OK · jArchi plugin not installed · renderer HTML report.", await archi.TestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Test_fails_when_jArchi_renderer_lacks_the_plugin()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        RequireDisplay();
        var (sp, _) = Build(new() { ["Archi:ExecutablePath"] = FakeArchi(), ["Archi:Renderer"] = "jarchi" });
        await Assert.ThrowsAsync<ArchiException>(() => sp.GetRequiredService<ArchiService>().TestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void JArchi_script_embeds_settings_as_json()
    {
        var script = ArchiService.JArchiScript(@"C:\t\images", ["Main \"x\""], 200, 5);
        Assert.Contains("var cfg = {\"dir\":\"C:/t/images\",\"views\":[\"Main \\u0022x\\u0022\"],\"scale\":2,\"margin\":5};", script);
        Assert.Contains("$.model.renderViewToFile(v,", script);
    }

    [Fact]
    public void Reads_views_from_zipped_archive_models()
    {
        var file = Path.Combine(_dir, "a.archimate");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("model.xml").Open()))
        {
            w.Write("""<m xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><element xsi:type="archimate:ArchimateDiagramModel" name="V" id="i1"/><element xsi:type="archimate:BusinessActor" name="A" id="i2"/></m>""");
        }

        Assert.Equal([("i1", "V")], ArchiService.ReadViews(file));
    }

    [Fact]
    public void Outputs_avoid_hidden_folders_except_the_configured_one()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Throws<ArgumentException>(() => ArchiPaths.ValidateOutput("~/.ssh/x.png", _dir, ".png"));
        Assert.Throws<ArgumentException>(() => ArchiPaths.ValidateOutput(Path.Combine(_dir, "x.exe"), _dir, ".png"));
        var trusted = Path.Combine(home, ".cache", "archi");
        Assert.Equal(Path.Combine(trusted, "v.png"), ArchiPaths.ValidateOutput(Path.Combine(trusted, "v.png"), trusted, ".png"));
        Assert.Throws<ArgumentException>(() => ArchiService.ValidateModelPath(Path.Combine(_dir, "x.txt")));
    }
}
