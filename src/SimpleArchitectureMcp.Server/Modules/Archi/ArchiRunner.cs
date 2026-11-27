using System.Diagnostics;
using System.Text;

namespace SimpleArchitectureMcp.Server.Modules.Archi;

/// <summary>
/// Runs Archi's headless command line (<c>-application com.archimatetool.commandline.app</c>). Archi is an SWT
/// application and needs a display even headless; on Linux without one, the run is wrapped in <c>xvfb-run</c>.
/// </summary>
public sealed class ArchiRunner
{
    public const string Application = "com.archimatetool.commandline.app";

    public sealed record Result(int ExitCode, string Output)
    {
        /// <summary>The last lines of Archi's console output, for error messages.</summary>
        public string Tail(int lines = 12) =>
            string.Join('\n', Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));
    }

    public async Task<Result> RunAsync(string executable, IReadOnlyList<string> arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var (file, prefix) = Launcher(executable);
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };
        foreach (var a in prefix.Concat(["-application", Application, "-consoleLog", "-nosplash", "--abortOnException"]).Concat(arguments))
        {
            start.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ArchiException($"Cannot start {file}: {ex.Message}");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"Archi did not finish within {timeoutSeconds}s (Archi:TimeoutSeconds).");
        }

        process.WaitForExit(); // flush the async output readers
        lock (output)
        {
            return new Result(process.ExitCode, output.ToString());
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            if (output.Length < 64 * 1024)
            {
                output.AppendLine(line);
            }
        }
    }

    /// <summary>The process to start and the arguments that precede Archi's own.</summary>
    internal static (string File, string[] Prefix) Launcher(string executable)
    {
        if (!OperatingSystem.IsLinux()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return (executable, []);
        }

        var xvfb = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "xvfb-run"))
            .FirstOrDefault(File.Exists);
        return xvfb is null
            ? throw new ArchiException("Archi needs a display. Install Xvfb (xvfb-run) or run simple-architecture-mcp in a desktop session.")
            : (xvfb, ["-a", executable]); // xvfb-run -a Archi ...
    }
}
