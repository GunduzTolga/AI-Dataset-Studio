using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;

namespace AIDatasetStudio.Infrastructure.Training;

/// <summary>
/// Runs <c>llamafactory-cli train</c> or <c>axolotl train</c> on an exported folder as a child
/// process and reads its output as it comes (M28, ADR-048). The tool, Python and the GPU drivers
/// are the user's; the app installs nothing.
/// </summary>
internal sealed partial class ProcessTrainingRunner(ITrainingToolSettings settings) : ITrainingRunner
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
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim('"'));

        // pip on Windows puts commands in a Scripts folder that is often not on PATH.
        if (OperatingSystem.IsWindows())
        {
            folders = folders.Concat(WindowsScriptFolders(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        }

        return FindIn(folders, names);
    }

    /// <summary>
    /// The first folder that has one of the commands.
    /// </summary>
    internal static string? FindIn(IEnumerable<string> folders, IReadOnlyList<string> names)
    {
        foreach (var folder in folders)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(folder, name);
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

    /// <summary>
    /// Where Python on Windows installs commands, newest Python first: python.org's installer (for the
    /// user and for everyone), pip's --user folder, the Microsoft Store's Python, Miniconda and
    /// Anaconda with their environments, and a venv in a folder of the user's own
    /// (C:\Users\me\LLaMA-Factory\.venv).
    /// </summary>
    internal static IEnumerable<string> WindowsScriptFolders(string userProfile, string localAppData, string appData, string programFiles)
    {
        // By Python version, not by name: "Python39" sorts after "Python313" as text.
        IEnumerable<string> Under(string root, string pattern) => Folders(root, pattern)
            .OrderByDescending(PythonMinorVersion).ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var python in Under(Path.Combine(localAppData, "Programs", "Python"), "Python3*").Concat(Under(programFiles, "Python3*")))
        {
            yield return Path.Combine(python, "Scripts");
        }

        foreach (var python in Under(Path.Combine(appData, "Python"), "Python3*"))
        {
            yield return Path.Combine(python, "Scripts");
        }

        foreach (var store in Under(Path.Combine(localAppData, "Packages"), "PythonSoftwareFoundation.Python.3*"))
        {
            foreach (var python in Under(Path.Combine(store, "LocalCache", "local-packages"), "Python3*"))
            {
                yield return Path.Combine(python, "Scripts");
            }
        }

        var condas = new[] { userProfile, localAppData }.Where(root => root.Length > 0)
            .SelectMany(root => new[] { "miniconda3", "anaconda3", "miniforge3" }.Select(name => Path.Combine(root, name)))
            .Where(Directory.Exists);
        foreach (var conda in condas)
        {
            yield return Path.Combine(conda, "Scripts");
            foreach (var environment in Under(Path.Combine(conda, "envs"), "*"))
            {
                yield return Path.Combine(environment, "Scripts");
            }
        }

        foreach (var folder in Folders(userProfile, "*").Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(folder, "Scripts");
            yield return Path.Combine(folder, "venv", "Scripts");
            yield return Path.Combine(folder, ".venv", "Scripts");
        }
    }

    // 13 for "Python313", "Python313-32" and "PythonSoftwareFoundation.Python.3.13_…"; -1 for other folders.
    private static int PythonMinorVersion(string folder) =>
        PythonVersion().Match(Path.GetFileName(folder)) is { Success: true } version
        && int.TryParse(version.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ? minor : -1;

    [GeneratedRegex(@"^(?:.*\.)?Python\.?3\.?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PythonVersion();

    private static IEnumerable<string> Folders(string root, string pattern)
    {
        try
        {
            return root.Length > 0 && Directory.Exists(root) ? Directory.GetDirectories(root, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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

        // The trainer states its step count ("Total optimization steps = N") at INFO, which tells its
        // progress bar from the others. LLaMA-Factory shows INFO; Axolotl shows other libraries'
        // messages from WARNING up unless LOG_LEVEL lowers that, and Transformers needs its own level.
        if (request.Framework == TrainingFramework.Axolotl)
        {
            start.Environment["TRANSFORMERS_VERBOSITY"] = "info";
            start.Environment["LOG_LEVEL"] = "INFO";
        }

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
        async Task PumpAsync(StreamReader stream, TrainingOutputStream from)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                // Reported in the order read, whichever stream it came from.
                lock (gate)
                {
                    Report(reader.Read(buffer.AsSpan(0, read), from));
                }
            }
        }

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, TrainingOutputStream.StandardOutput),
            PumpAsync(process.StandardError, TrainingOutputStream.StandardError));
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
