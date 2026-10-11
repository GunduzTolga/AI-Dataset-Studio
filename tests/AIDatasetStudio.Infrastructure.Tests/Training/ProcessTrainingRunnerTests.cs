using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Infrastructure.Training;

namespace AIDatasetStudio.Infrastructure.Tests.Training;

/// <summary>
/// Running a training tool as a child process (M28): a script stands in for the tool, a shell
/// script on Linux and macOS and a batch file on Windows.
/// </summary>
public sealed class ProcessTrainingRunnerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"), "eğitim klasörü");
    private readonly ToolPaths _settings = new();

    public ProcessTrainingRunnerTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, TrainingConfigs.LlamaFactoryFile), "model_name_or_path: \"x\"\noutput_dir: \"saves/destek/lora/sft\"  # where the adapter goes\n");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_tool_trains_in_the_folder_on_its_configuration_and_its_progress_is_read_as_it_runs()
    {
        var tool = WriteTool("ok", exitCode: 0, waits: false, extraLine: null);
        var runner = new ProcessTrainingRunner(_settings);
        var events = new List<TrainingEvent>();

        var result = await runner.RunAsync(new TrainingRequest(TrainingFramework.LlamaFactory, _folder, tool), new Inline(events.Add), Ct);

        Assert.True(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.Equal(Path.GetFullPath(Path.Combine(_folder, "saves", "destek", "lora", "sft")), result.OutputDirectory);
        var log = events.OfType<TrainingLogLine>().Select(l => l.Text).ToList();
        Assert.Contains("args: train llamafactory.yaml", log);
        // The folder's own name may come back in the console's code page on Windows; its parent's is plain ASCII.
        Assert.Contains(log, l => l.StartsWith("cwd: ", StringComparison.Ordinal) && l.Contains(Path.GetFileName(Path.GetDirectoryName(_folder)!), StringComparison.Ordinal));
        Assert.Equal(new TrainingStep(2, 4, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)), Assert.Single(events.OfType<TrainingStep>()));
        Assert.Equal(new TrainingMetrics(0.5, 1.5, null, 1e-4), Assert.Single(events.OfType<TrainingMetrics>()));
    }

    [Fact]
    public async Task A_failed_run_is_named_from_its_last_lines()
    {
        var tool = WriteTool("oom", exitCode: 3, waits: false, extraLine: "torch.OutOfMemoryError: CUDA out of memory. Tried to allocate 2.00 GiB");

        var result = await new ProcessTrainingRunner(_settings).RunAsync(new TrainingRequest(TrainingFramework.LlamaFactory, _folder, tool), new Inline(_ => { }), Ct);

        Assert.Equal((3, false, TrainingFailure.OutOfMemory), (result.ExitCode, result.Succeeded, result.Failure));
        Assert.Contains(result.LastLines, l => l.Contains("CUDA out of memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopping_ends_the_tool_and_what_it_started()
    {
        var tool = WriteTool("long", exitCode: 0, waits: true, extraLine: null);
        using var stop = new CancellationTokenSource();
        var started = DateTime.UtcNow;

        var result = await new ProcessTrainingRunner(_settings).RunAsync(
            new TrainingRequest(TrainingFramework.LlamaFactory, _folder, tool),
            new Inline(e =>
            {
                if (e is TrainingMetrics)
                {
                    stop.Cancel();
                }
            }),
            stop.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_tool_that_cannot_be_started_is_reported_as_missing()
    {
        var result = await new ProcessTrainingRunner(_settings).RunAsync(
            new TrainingRequest(TrainingFramework.LlamaFactory, _folder, Path.Combine(_folder, "yok", "llamafactory-cli")), new Inline(_ => { }), Ct);

        Assert.Equal(TrainingFailure.ToolNotFound, result.Failure);
    }

    [Fact]
    public void The_chosen_tool_is_used_and_the_folder_tells_which_tool_it_is_for()
    {
        var runner = new ProcessTrainingRunner(_settings);
        var tool = WriteTool("chosen", exitCode: 0, waits: false, extraLine: null);
        _settings.Set(TrainingFramework.Axolotl, tool);

        Assert.Equal(tool, runner.FindTool(TrainingFramework.Axolotl));
        Assert.Equal(TrainingFramework.LlamaFactory, runner.DetectFramework(_folder));
        File.Delete(Path.Combine(_folder, TrainingConfigs.LlamaFactoryFile));
        File.WriteAllText(Path.Combine(_folder, TrainingConfigs.AxolotlFile), "base_model: x\n");
        Assert.Equal(TrainingFramework.Axolotl, runner.DetectFramework(_folder));
        File.Delete(Path.Combine(_folder, TrainingConfigs.AxolotlFile));
        Assert.Null(runner.DetectFramework(_folder));

        // A chosen tool that is gone is not offered.
        _settings.Set(TrainingFramework.Axolotl, Path.Combine(_folder, "gone"));
        Assert.NotEqual(Path.Combine(_folder, "gone"), runner.FindTool(TrainingFramework.Axolotl));
    }

    [Fact]
    public void On_Windows_the_tool_is_also_looked_for_where_Python_installs_commands()
    {
        var root = Path.Combine(_folder, "windows");
        string Make(params string[] parts)
        {
            var path = Path.Combine([root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        var (home, local, roaming, programs) = (Make("Users", "me"), Make("Local"), Make("Roaming"), Make("Program Files"));
        Make("Local", "Programs", "Python", "Python311");
        Make("Local", "Programs", "Python", "Python312");
        Make("Roaming", "Python", "Python312");
        Make("Local", "Packages", "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0", "LocalCache", "local-packages", "Python312");
        Make("Users", "me", "miniconda3", "envs", "egitim");
        Make("Users", "me", "LLaMA-Factory", ".venv", "Scripts");

        var folders = ProcessTrainingRunner.WindowsScriptFolders(home, local, roaming, programs).ToList();

        Assert.Equal(
            [
                Path.Combine(local, "Programs", "Python", "Python312", "Scripts"),
                Path.Combine(local, "Programs", "Python", "Python311", "Scripts"),
                Path.Combine(roaming, "Python", "Python312", "Scripts"),
                Path.Combine(local, "Packages", "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0", "LocalCache", "local-packages", "Python312", "Scripts"),
                Path.Combine(home, "miniconda3", "Scripts"),
                Path.Combine(home, "miniconda3", "envs", "egitim", "Scripts"),
            ],
            folders.Take(6));
        var venv = Path.Combine(home, "LLaMA-Factory", ".venv", "Scripts");
        Assert.Contains(venv, folders);

        var tool = Path.Combine(venv, "llamafactory-cli.exe");
        File.WriteAllText(tool, "");
        Assert.Equal(tool, ProcessTrainingRunner.FindIn(folders, ["llamafactory-cli.exe", "llamafactory-cli.cmd"]));
        Assert.Empty(ProcessTrainingRunner.WindowsScriptFolders(Path.Combine(root, "nobody"), "", "", ""));
    }

    [Theory]
    [InlineData(17163091968L, 17163091968L)]
    [InlineData(-1, 4294967295L)]
    public void A_memory_size_is_read_however_the_registry_keeps_it(long stored, long bytes)
    {
        Assert.Equal(bytes, stored > uint.MaxValue ? WindowsGpuMemory.Bytes(stored) : WindowsGpuMemory.Bytes((int)stored));
        Assert.Equal(bytes, stored > uint.MaxValue ? WindowsGpuMemory.Bytes(BitConverter.GetBytes(stored)) : WindowsGpuMemory.Bytes(BitConverter.GetBytes((int)stored)));
        Assert.Null(WindowsGpuMemory.Bytes("16 GB"));
    }

    [Fact]
    public async Task The_GPU_monitor_answers_on_any_system_with_a_plausible_reading_or_none()
    {
        // On a build machine there may be no GPU at all; whatever it reads must make sense.
        var memory = await new GpuMonitor().ReadAsync(Ct);

        if (memory is not null)
        {
            Assert.True(memory.TotalMb > 0);
            Assert.InRange(memory.UsedMb, 0, memory.TotalMb * 2);
        }
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4050 Laptop GPU, 1234, 6141\n", "NVIDIA GeForce RTX 4050 Laptop GPU", 1234L, 6141L)]
    [InlineData("", null, 0L, 0L)]
    public void Nvidia_memory_is_read(string output, string? name, long used, long total) =>
        Assert.Equal(name is null ? null : new GpuMemory(name, used, total), GpuMonitor.ParseNvidia(output));

    [Fact]
    public void Rocm_memory_is_read_from_its_columns()
    {
        const string output = "\n============================ ROCm System Management Interface ============================\n"
            + "device,Card Series,Card Model,VRAM Total Memory (B),VRAM Total Used Memory (B)\n"
            + "card0,AMD Radeon RX 7800 XT,0x747e,17163091968,2147483648\n";

        Assert.Equal(new GpuMemory("AMD Radeon RX 7800 XT", 2048, 16368), GpuMonitor.ParseRocm(output));
        Assert.Null(GpuMonitor.ParseRocm("device,Something else\ncard0,1\n"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // A stand-in for llamafactory-cli: echoes its arguments and folder, a progress bar and a loss.
    private string WriteTool(string name, int exitCode, bool waits, string? extraLine)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_folder, name + ".cmd");
            File.WriteAllText(path, string.Join("\r\n",
                "@echo off",
                "echo args: %1 %2",
                "echo cwd: %CD%",
                "echo   Total optimization steps = 4",
                "echo  50%%^|  ^| 2/4 [00:02^<00:02, 1.0it/s] 1>&2",
                "echo {'loss': '1.5', 'learning_rate': '1e-4', 'epoch': '0.5'}",
                extraLine is null ? "" : "echo " + extraLine,
                waits ? "ping -n 60 127.0.0.1 >nul" : "",
                $"exit /b {exitCode}",
                ""));
            return path;
        }

        var script = Path.Combine(_folder, name);
        File.WriteAllText(script, string.Join("\n",
            "#!/bin/sh",
            "echo \"args: $1 $2\"",
            "echo \"cwd: $(pwd)\"",
            "echo '  Total optimization steps = 4'",
            "printf ' 50%%|  | 2/4 [00:02<00:02, 1.0it/s]\\r' >&2",
            "echo \"{'loss': '1.5', 'learning_rate': '1e-4', 'epoch': '0.5'}\"",
            extraLine is null ? "" : $"echo '{extraLine}'",
            waits ? "sleep 60" : "",
            $"exit {exitCode}",
            ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private sealed class Inline(Action<TrainingEvent> report) : IProgress<TrainingEvent>
    {
        public void Report(TrainingEvent value) => report(value);
    }

    private sealed class ToolPaths : ITrainingToolSettings
    {
        private readonly Dictionary<TrainingFramework, string> _paths = [];

        public string? GetToolPath(TrainingFramework framework) => _paths.GetValueOrDefault(framework);

        public void SetToolPath(TrainingFramework framework, string? path) => Set(framework, path);

        public void Set(TrainingFramework framework, string? path)
        {
            if (path is null)
            {
                _paths.Remove(framework);
            }
            else
            {
                _paths[framework] = path;
            }
        }
    }
}
