using System.ComponentModel;
using System.Diagnostics;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;

namespace AIDatasetStudio.Infrastructure.Training;

/// <summary>
/// Runs <c>llamafactory-cli train</c> or <c>axolotl train</c> on an exported folder as a child
/// process and reads its output as it comes (M28, ADR-048). The tool, Python and the GPU drivers
/// are the user's; the app installs nothing.
/// </summary>
internal sealed class ProcessTrainingRunner(ITrainingToolSettings settings) : ITrainingRunner
{
    /// <summary>The command each tool installs.</summary>
    public static string CommandName(TrainingFramework framework) =>
        framework == TrainingFramework.Axolotl ? "axolotl" : "llamafactory-cli";

    public string? FindTool(TrainingFramework framework)
    {
        if (settings.GetToolPath(framework) is { } chosen && File.Exists(chosen))
        {
            return chosen;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { CommandName(framework) + ".exe", CommandName(framework) + ".cmd" }
            : [CommandName(framework)];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A PATH entry that is not a valid folder name.
                }
            }
        }

        return null;
    }

    public TrainingFramework? DetectFramework(string folder) =>
        File.Exists(Path.Combine(folder, TrainingConfigs.LlamaFactoryFile)) ? TrainingFramework.LlamaFactory
        : File.Exists(Path.Combine(folder, TrainingConfigs.AxolotlFile)) ? TrainingFramework.Axolotl
        : null;

    public async Task<TrainingResult> RunAsync(TrainingRequest request, IProgress<TrainingEvent> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        var config = TrainingConfigs.FileNames(request.Framework)[^1];
        var start = new ProcessStartInfo(request.ToolPath)
        {
            WorkingDirectory = request.Folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        start.ArgumentList.Add("train");
        start.ArgumentList.Add(config);

        // Output as it is written, in UTF-8 whatever the console's code page.
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        var reader = new TrainingOutputReader();
        var gate = new Lock();
        void Report(IReadOnlyList<TrainingEvent> events)
        {
            foreach (var item in events)
            {
                progress.Report(item);
            }
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return new TrainingResult(-1, Cancelled: false, OutputDirectory(request.Folder, config), TrainingFailure.ToolNotFound, []);
        }

        // Both streams feed one reader: the progress bars go to stderr, the trainer's logs to either.
        async Task PumpAsync(StreamReader stream)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                // Reported in the order read, whichever stream it came from.
                lock (gate)
                {
                    Report(reader.Read(buffer.AsSpan(0, read)));
                }
            }
        }

        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await pumps.ConfigureAwait(false);
        lock (gate)
        {
            Report(reader.Finish());
        }

        var lines = reader.LastLines;
        var failure = !cancelled && process.ExitCode != 0 ? TrainingOutputReader.Classify(lines) : (TrainingFailure?)null;
        return new TrainingResult(process.ExitCode, cancelled, OutputDirectory(request.Folder, config), failure, lines);
    }

    // The tool and every process it started (data loaders, distributed workers).
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It had already exited.
        }
    }

    /// <summary>
    /// The configuration's <c>output_dir</c>, relative to the folder it is in.
    /// </summary>
    internal static string? OutputDirectory(string folder, string config)
    {
        var path = Path.Combine(folder, config);
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("output_dir:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["output_dir:".Length..];
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            value = (comment >= 0 ? value[..comment] : value).Trim().Trim('"', '\'');
            return value.Length == 0 ? null : Path.GetFullPath(Path.Combine(folder, value));
        }

        return null;
    }
}
