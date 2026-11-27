using System.IO.Compression;
using System.Text;
using SimpleArchitectureMcp.Server.Core.Modules;

namespace SimpleArchitectureMcp.Server.Modules.Archi;

/// <summary>
/// Settings-page conversions of an uploaded model. Results are returned as downloads and never written to the
/// output folder.
/// </summary>
public sealed partial class ArchiService
{
    /// <summary>All views as PNG: one view gives a .png, several a .zip of PNGs.</summary>
    public async Task<ConversionFile> DownloadPngAsync(string model, CancellationToken cancellationToken)
    {
        var input = ValidateModelPath(model);
        var target = Path.Combine(Core.Security.OwnTempFolder.Path, "archi-png-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var rendered = await RenderToAsync(input, null, target, cancellationToken).ConfigureAwait(false);
            if (rendered.Count == 1)
            {
                return new ConversionFile(Path.GetFileName(rendered[0].Path), "image/png",
                    await File.ReadAllBytesAsync(rendered[0].Path, cancellationToken).ConfigureAwait(false));
            }

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var view in rendered)
                {
                    // PNG is already compressed.
                    zip.CreateEntryFromFile(view.Path, Path.GetFileName(view.Path), CompressionLevel.NoCompression);
                }
            }

            return new ConversionFile(Path.GetFileNameWithoutExtension(input) + ".zip", "application/zip", buffer.ToArray());
        }
        finally
        {
            TryDelete(target);
        }
    }

    /// <summary>All views as one draw.io file, a page per view.</summary>
    public async Task<ConversionFile> DownloadDrawioAsync(string model, CancellationToken cancellationToken)
    {
        Require();
        var input = ValidateModelPath(model);
        var result = DrawioConverter.Convert(await ReadExchangeXmlAsync(input, cancellationToken).ConfigureAwait(false), null);
        return new ConversionFile(Path.GetFileNameWithoutExtension(input) + ".drawio", "application/vnd.jgraph.mxfile",
            new UTF8Encoding(false).GetBytes(result.Xml));
    }

    /// <summary>Open Exchange .xml becomes an Archi .archimate model, and an .archimate model becomes exchange .xml.</summary>
    public async Task<ConversionFile> DownloadOtherFormatAsync(string model, CancellationToken cancellationToken)
    {
        Require();
        var input = ValidateModelPath(model);
        var name = Path.GetFileNameWithoutExtension(input);
        return input.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? new ConversionFile(name + ".archimate", "application/octet-stream",
                await RunArchiForFileAsync(["--xmlexchange.import", input, "--saveModel"], "model.archimate", input, cancellationToken).ConfigureAwait(false))
            : new ConversionFile(name + ".xml", "application/xml",
                Encoding.UTF8.GetBytes(await ReadExchangeXmlAsync(input, cancellationToken).ConfigureAwait(false)));
    }
}
