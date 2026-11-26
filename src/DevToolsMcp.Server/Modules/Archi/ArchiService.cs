using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Primitives;

namespace DevToolsMcp.Server.Modules.Archi;

/// <summary>
/// Renders the views of an Archi model (.archimate) or an Open Exchange Format file (.xml) to PNG, and writes
/// exchange files from <see cref="ArchiModelSpec"/>. One Archi process runs at a time.
/// </summary>
public sealed partial class ArchiService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ArchiRunner _runner;
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State _state;

    public ArchiService(IConfiguration configuration, ArchiRunner runner)
    {
        _configuration = configuration;
        _runner = runner;
        _state = Build();
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, () => Volatile.Write(ref _state, Build()));
    }

    /// <param name="Executable">null when Archi is not (validly) configured.</param>
    public sealed record State(ArchiOptions Options, string? Executable, string Message);

    public sealed record RenderedView(string Id, string Name, string Path, int Width, int Height);

    public State Current => Volatile.Read(ref _state);

    private State Build()
    {
        var options = new ArchiOptions();
        _configuration.GetSection(ArchiOptions.SectionName).Bind(options); // source-generated binder (AOT-safe)
        return options.TryValidate(out var executable, out var reason)
            ? new State(options, executable, $"{(options.UseJArchi ? "jArchi" : "HTML report")} renderer · output {options.OutputPath}")
            : new State(options, null, reason!);
    }

    private (ArchiOptions Options, string Executable) Require()
    {
        var state = Current;
        return state.Executable is null ? throw new ArchiNotConfiguredException(state.Message) : (state.Options, state.Executable);
    }

    /// <summary>Writes the exchange file for <paramref name="spec"/>; returns its path and a one-line summary.</summary>
    public async Task<(string Path, string Summary)> CreateModelAsync(ArchiModelSpec spec, string? path, CancellationToken cancellationToken)
    {
        var (options, _) = Require();
        var result = ExchangeModelBuilder.Build(spec);
        var file = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(options.OutputPath, ArchiPaths.FileName(string.IsNullOrWhiteSpace(spec.Name) ? "model" : spec.Name) + ".xml")
            : ArchiPaths.ValidateOutput(path, options.OutputPath, ".xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, result.Xml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return (file, $"{result.Elements} elements, {result.Relationships} relationships, views: {string.Join(", ", result.Views)}");
    }

    /// <summary>Renders the named views (all when empty) of a model file to PNG files in <paramref name="outDir"/>.</summary>
    public Task<IReadOnlyList<RenderedView>> RenderAsync(string model, string[]? views, string? outDir, CancellationToken cancellationToken)
    {
        var options = Require().Options;
        var input = ValidateModelPath(model);
        var target = string.IsNullOrWhiteSpace(outDir) ? options.OutputPath : ArchiPaths.ValidateOutput(outDir, options.OutputPath);
        return RenderToAsync(input, views, target, cancellationToken);
    }

    /// <summary>Renders into <paramref name="target"/>, which the caller has validated (or owns, like a temp folder).</summary>
    private async Task<IReadOnlyList<RenderedView>> RenderToAsync(string input, string[]? views, string target, CancellationToken cancellationToken)
    {
        var (options, executable) = Require();
        var wanted = (views ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();

        var work = Path.Combine(Path.GetTempPath(), "devtools-mcp", "archi-run-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Saving the loaded model gives the real view ids and names, also for imported exchange files.
            var saved = Path.Combine(work, "model.archimate");
            var images = Path.Combine(work, "images");
            List<string> args = input.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? ["--xmlexchange.import", input]
                : ["--loadModel", input];
            args.AddRange(["--saveModel", saved]);
            if (options.UseJArchi)
            {
                Directory.CreateDirectory(images);
                var script = Path.Combine(work, "render.ajs");
                await File.WriteAllTextAsync(script, JArchiScript(images, wanted, options.ScalePercent, options.MarginPixels), cancellationToken)
                    .ConfigureAwait(false);
                args.AddRange(["--script.runScript", script]);
            }
            else
            {
                args.AddRange(["--html.createReport", Path.Combine(work, "report")]);
            }

            var run = await _runner.RunAsync(executable, args, options.Timeout, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(saved))
            {
                throw new ArchiException($"Archi could not load {Path.GetFileName(input)} (exit {run.ExitCode}):\n{run.Tail()}");
            }

            var all = ReadViews(saved);
            var selected = Select(all, wanted);
            var rendered = new List<RenderedView>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(target);
            foreach (var (id, name) in selected)
            {
                var png = FindImage(work, id)
                    ?? throw new ArchiException($"Archi produced no image for view '{name}' (exit {run.ExitCode}):\n{run.Tail()}");
                var fileName = ArchiPaths.FileName(name);
                for (var i = 2; !names.Add(fileName); i++)
                {
                    fileName = $"{ArchiPaths.FileName(name)}-{i}";
                }

                var dest = Path.Combine(target, fileName + ".png");
                File.Copy(png, dest, overwrite: true);
                var (w, h) = PngSize(dest);
                rendered.Add(new RenderedView(id, name, dest, w, h));
            }

            return rendered;
        }
        finally
        {
            _gate.Release();
            TryDelete(work);
        }
    }

    /// <summary>
    /// Writes a draw.io file with one page per selected view. Exchange .xml is converted directly; an .archimate
    /// model is first exported to exchange format by Archi.
    /// </summary>
    public async Task<(string Path, IReadOnlyList<string> Pages)> ToDrawioAsync(string model, string[]? views, string? path, CancellationToken cancellationToken)
    {
        var options = Require().Options;
        var input = ValidateModelPath(model);
        var file = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(options.OutputPath, Path.GetFileNameWithoutExtension(input) + ".drawio")
            : ArchiPaths.ValidateOutput(path, options.OutputPath, ".drawio");

        var result = DrawioConverter.Convert(await ReadExchangeXmlAsync(input, cancellationToken).ConfigureAwait(false), views);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, result.Xml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return (file, result.Pages);
    }

    /// <summary>The model in Open Exchange format: an exchange .xml is read as is, an .archimate model is exported by Archi.</summary>
    private async Task<string> ReadExchangeXmlAsync(string input, CancellationToken cancellationToken)
    {
        if (input.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return await File.ReadAllTextAsync(input, cancellationToken).ConfigureAwait(false);
        }

        var bytes = await RunArchiForFileAsync(["--loadModel", input, "--xmlexchange.export"], "model.xml", input, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Runs Archi with <paramref name="args"/> followed by an output file in a temp folder, and returns that file.</summary>
    private async Task<byte[]> RunArchiForFileAsync(string[] args, string outputName, string input, CancellationToken cancellationToken)
    {
        var (options, executable) = Require();
        var work = Path.Combine(Path.GetTempPath(), "devtools-mcp", "archi-run-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var output = Path.Combine(work, outputName);
            var run = await _runner.RunAsync(executable, [.. args, output], options.Timeout, cancellationToken).ConfigureAwait(false);
            return File.Exists(output)
                ? await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false)
                : throw new ArchiException($"Archi could not convert {Path.GetFileName(input)} (exit {run.ExitCode}):\n{run.Tail()}");
        }
        finally
        {
            _gate.Release();
            TryDelete(work);
        }
    }

    /// <summary>Checks that Archi's command line answers and reports whether the jArchi plugin is installed.</summary>
    public async Task<string> TestAsync(CancellationToken cancellationToken)
    {
        var (options, executable) = Require();
        var run = await _runner.RunAsync(executable, ["--help"], Math.Min(options.Timeout, 60), cancellationToken).ConfigureAwait(false);
        if (!run.Output.Contains("--loadModel", StringComparison.Ordinal))
        {
            throw new ArchiException($"{executable} did not answer like Archi's command line (exit {run.ExitCode}):\n{run.Tail()}");
        }

        var jArchi = run.Output.Contains("--script.runScript", StringComparison.Ordinal);
        if (options.UseJArchi && !jArchi)
        {
            throw new ArchiException("Renderer is jArchi, but the jArchi scripting plugin is not installed in this Archi. Install it or switch the renderer to HtmlReport.");
        }

        return $"Archi command line OK · jArchi plugin {(jArchi ? "installed" : "not installed")} · renderer {(options.UseJArchi ? "jArchi" : "HTML report")}.";
    }

    internal static string ValidateModelPath(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("model (path to a .archimate or exchange .xml file) is required.");
        }

        var full = Path.GetFullPath(ArchiPaths.ExpandHome(model.Trim().Trim('"')));
        var ext = Path.GetExtension(full);
        if (!ext.Equals(".archimate", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{full}: expected a .archimate or Open Exchange .xml file.");
        }

        return File.Exists(full) ? full : throw new ArgumentException($"File not found: {full}");
    }

    /// <summary>Diagram views (id, name) of an .archimate file, in document order. Handles the zipped archive format.</summary>
    internal static List<(string Id, string Name)> ReadViews(string archimateFile)
    {
        using var stream = File.OpenRead(archimateFile);
        Span<byte> magic = stackalloc byte[2];
        var zipped = stream.Read(magic) == 2 && magic[0] == 'P' && magic[1] == 'K';
        stream.Position = 0;
        if (!zipped)
        {
            return ReadViews(stream);
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var entry = (zip.GetEntry("model.xml") ?? throw new ArchiException("Archive has no model.xml.")).Open();
        return ReadViews(entry);
    }

    private static List<(string Id, string Name)> ReadViews(Stream stream)
    {
        var views = new List<(string, string)>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "element")
            {
                continue;
            }

            var type = reader.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance") ?? string.Empty;
            if (type.EndsWith("DiagramModel", StringComparison.Ordinal) || type.EndsWith("SketchModel", StringComparison.Ordinal)
                || type.EndsWith("CanvasModel", StringComparison.Ordinal))
            {
                views.Add((reader.GetAttribute("id") ?? string.Empty, reader.GetAttribute("name") ?? "View"));
            }
        }

        return views;
    }

    internal static List<(string Id, string Name)> Select(List<(string Id, string Name)> all, string[] wanted)
    {
        if (all.Count == 0)
        {
            throw new ArchiException("The model has no views.");
        }

        if (wanted.Length == 0)
        {
            return all;
        }

        var selected = new List<(string, string)>();
        foreach (var w in wanted)
        {
            var match = all.Where(v => v.Id == w || string.Equals(v.Name, w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0)
            {
                throw new ArgumentException($"No view '{w}'. Views: {string.Join(", ", all.Select(v => v.Name).Take(30))}");
            }

            selected.AddRange(match.Where(m => !selected.Contains(m)));
        }

        return selected;
    }

    /// <summary>jArchi writes images/&lt;id&gt;.png; Archi's HTML report has report/images/&lt;id&gt;.png.</summary>
    private static string? FindImage(string work, string id)
    {
        string[] direct = [Path.Combine(work, "images", id + ".png"), Path.Combine(work, "report", "images", id + ".png")];
        return direct.FirstOrDefault(File.Exists)
            ?? Directory.EnumerateFiles(work, id + ".png", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>A jArchi script that renders the selected views of the model loaded on the command line.</summary>
    internal static string JArchiScript(string imagesDir, string[] views, int scalePercent, int margin)
    {
        static string Str(string v) => "\"" + JsonEncodedText.Encode(v) + "\"";
        var cfg = new StringBuilder("{\"dir\":").Append(Str(imagesDir.Replace('\\', '/')));
        cfg.Append(",\"views\":[").AppendJoin(',', views.Select(Str)).Append(']');
        cfg.Append(",\"scale\":").Append((scalePercent / 100.0).ToString(CultureInfo.InvariantCulture));
        cfg.Append(",\"margin\":").Append(margin.ToString(CultureInfo.InvariantCulture)).Append('}');
        return $$"""
            var cfg = {{cfg}};
            $(model).find("view").each(function (v) {
                if (cfg.views.length && cfg.views.indexOf(v.id) < 0 && !cfg.views.some(function (n) { return n.toLowerCase() === String(v.name).toLowerCase(); })) return;
                $.model.renderViewToFile(v, cfg.dir + "/" + v.id + ".png", "PNG", { scale: cfg.scale, margin: cfg.margin });
            });
            """;
    }

    internal static (int Width, int Height) PngSize(string file)
    {
        Span<byte> header = stackalloc byte[24];
        using var s = File.OpenRead(file);
        return s.Read(header) == 24
            ? (BinaryPrimitives.ReadInt32BigEndian(header[16..]), BinaryPrimitives.ReadInt32BigEndian(header[20..]))
            : (0, 0);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _gate.Dispose();
    }
}

public class ArchiException(string message) : InvalidOperationException(message);

public sealed class ArchiNotConfiguredException(string reason)
    : ArchiException($"Archi is not configured ({reason}). Open the devtools-mcp settings page.");
