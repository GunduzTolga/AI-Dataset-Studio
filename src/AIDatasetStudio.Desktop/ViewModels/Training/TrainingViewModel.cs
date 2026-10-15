using System.Globalization;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Training;

/// <summary>
/// The folder to train on: what an export wrote, data and configuration side by side.
/// </summary>
/// <param name="Name">What is trained, for the window's title: the dataset's name, or the folder's.</param>
public sealed record TrainingRunTarget(string Folder, TrainingFramework Framework, string Name);

/// <summary>
/// A point of a loss curve: the epoch (or step) and the loss.
/// </summary>
public sealed record ChartPoint(double X, double Y);

public enum TrainingState
{
    Ready,
    Running,
    Succeeded,
    Failed,
    Stopped,
}

/// <summary>
/// Trains a model on an exported folder with the tool installed on this computer and shows the
/// run as it goes: steps and time left, the loss curves, the GPU's memory and the tool's log
/// (M28, SDD §21 V5).
/// </summary>
public sealed partial class TrainingViewModel : ViewModelBase, IDisposable
{
    private const int LogLines = 400;
    private static readonly TimeSpan GpuInterval = TimeSpan.FromSeconds(2);

    private readonly ITrainingRunner _runner;
    private readonly IGpuMonitor _gpu;
    private readonly ITrainingToolSettings _tools;
    private readonly IFileDialogService _files;
    private readonly IShellService _shell;
    private readonly Localizer _localizer;
    private readonly Queue<string> _log = new();
    private readonly List<ChartPoint> _train = [];
    private readonly List<ChartPoint> _eval = [];
    private CancellationTokenSource? _run;
    private TimeSpan? _elapsed;

    public TrainingViewModel(
        ITrainingRunner runner, IGpuMonitor gpu, ITrainingToolSettings tools, IFileDialogService files, IShellService shell, Localizer localizer, TrainingRunTarget target)
    {
        _runner = runner;
        _gpu = gpu;
        _tools = tools;
        _files = files;
        _shell = shell;
        _localizer = localizer;
        Target = target;
        ToolPath = runner.FindTool(target.Framework) ?? string.Empty;
    }

    /// <summary>
    /// Raised when the window should close: after the run was stopped to close it.
    /// </summary>
    public event EventHandler? CloseRequested;

    public TrainingRunTarget Target { get; }

    public string TitleText => _localizer.Format("Training_Target", Target.Name, ToolName);

    public string ToolName => Target.Framework == TrainingFramework.Axolotl ? "Axolotl" : "LLaMA-Factory";

    public string ToolLabel => _localizer.Format("Training_ToolLabel", ToolName);

    /// <summary>
    /// The tool's command, found on PATH or chosen by the user.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTool), nameof(CommandText), nameof(ToolHint))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string ToolPath { get; set; }

    public bool HasTool => !string.IsNullOrWhiteSpace(ToolPath);

    /// <summary>
    /// What runs: "llamafactory-cli train llamafactory.yaml".
    /// </summary>
    public string CommandText => $"{(HasTool ? Path.GetFileName(ToolPath.Trim()) : ProcessCommand)} train {TrainingConfigs.FileNames(Target.Framework)[^1]}";

    public string ToolHint => HasTool
        ? _localizer.Format("Training_ToolHint", Target.Folder)
        : _localizer.Format("Training_ToolMissing", ToolName, Target.Framework == TrainingFramework.Axolotl ? "pip install axolotl" : "pip install llamafactory");

    private string ProcessCommand => Target.Framework == TrainingFramework.Axolotl ? "axolotl" : "llamafactory-cli";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsIdle), nameof(IsSucceeded), nameof(IsFailed), nameof(IsStopped), nameof(HasResult), nameof(StateText), nameof(IsPreparing))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial TrainingState State { get; private set; }

    public bool IsRunning => State == TrainingState.Running;

    public bool IsIdle => !IsRunning;

    public bool IsSucceeded => State == TrainingState.Succeeded;

    public bool IsFailed => State == TrainingState.Failed;

    public bool IsStopped => State == TrainingState.Stopped;

    public bool HasResult => State is TrainingState.Succeeded or TrainingState.Failed or TrainingState.Stopped;

    public string StateText => _localizer[$"Training_State{State}"];

    // ===== Progress =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(StepText), nameof(HasSteps))]
    public partial int Step { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(StepText), nameof(HasSteps), nameof(IsPreparing))]
    public partial int TotalSteps { get; private set; }

    public bool HasSteps => TotalSteps > 0;

    /// <summary>
    /// Running but not training yet: the model and the data are being loaded.
    /// </summary>
    public bool IsPreparing => IsRunning && TotalSteps == 0;

    public double Percent => TotalSteps > 0 ? 100.0 * Step / TotalSteps : 0;

    /// <summary>
    /// "Step 30 / 66 · 45%".
    /// </summary>
    public string StepText => TotalSteps > 0
        ? _localizer.Format("Training_Step", Step, TotalSteps, Percent.ToString("0", CultureInfo.CurrentCulture))
        : _localizer[IsRunning ? "Training_Preparing" : "Training_NoSteps"];

    [ObservableProperty]
    public partial string TimeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string EpochText { get; private set; } = "–";

    [ObservableProperty]
    public partial string LossText { get; private set; } = "–";

    [ObservableProperty]
    public partial string EvalLossText { get; private set; } = "–";

    [ObservableProperty]
    public partial string LearningRateText { get; private set; } = "–";

    /// <summary>The training loss by epoch, as logged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurve))]
    public partial IReadOnlyList<ChartPoint> TrainPoints { get; private set; } = [];

    /// <summary>The validation loss after each evaluation.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartPoint> EvalPoints { get; private set; } = [];

    public bool HasCurve => TrainPoints.Count > 0;

    // ===== GPU =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGpu))]
    public partial string? GpuText { get; private set; }

    [ObservableProperty]
    public partial double GpuPercent { get; private set; }

    public bool HasGpu => GpuText is not null;

    // ===== Log and result =====

    [ObservableProperty]
    public partial string LogText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowLog { get; set; }

    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputCommand))]
    public partial string? OutputDirectory { get; private set; }

    /// <summary>
    /// Closing was asked for while the run goes on: the window asks before stopping it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsConfirmingClose { get; set; }

    /// <summary>
    /// The run in progress, for tests and for closing.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private async Task BrowseToolAsync()
    {
        if (await _files.PickTrainingToolAsync() is { } path)
        {
            ToolPath = path;
        }
    }

    private bool CanStart() => IsIdle && HasTool;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync()
    {
        PendingWork = RunAsync();
        return PendingWork;
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _run?.Cancel();

    private bool CanOpenOutput() => OutputDirectory is not null && Directory.Exists(OutputDirectory);

    [RelayCommand(CanExecute = nameof(CanOpenOutput))]
    private Task OpenOutputAsync() => _shell.OpenFolderAsync(OutputDirectory!);

    /// <summary>
    /// The window is being closed. While training runs it asks first; returns whether it may close now.
    /// </summary>
    public bool TryClose()
    {
        if (!IsRunning)
        {
            return true;
        }

        IsConfirmingClose = true;
        return false;
    }

    [RelayCommand]
    private void KeepRunning() => IsConfirmingClose = false;

    [RelayCommand]
    private async Task StopAndCloseAsync()
    {
        IsConfirmingClose = false;
        _run?.Cancel();
        await PendingWork;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync()
    {
        var tool = ToolPath.Trim();
        if (_runner.FindTool(Target.Framework) != tool)
        {
            _tools.SetToolPath(Target.Framework, tool);
        }

        Reset();
        using var run = new CancellationTokenSource();
        _run = run;
        State = TrainingState.Running;
        var gpu = WatchGpuAsync(run.Token);
        try
        {
            var result = await _runner.RunAsync(new TrainingRequest(Target.Framework, Target.Folder, tool), new Progress<TrainingEvent>(Apply), run.Token);
            OutputDirectory = result.OutputDirectory;
            (State, ResultText) = result switch
            {
                { Cancelled: true } => (TrainingState.Stopped, _localizer["Training_Stopped"]),
                { Succeeded: true } => (TrainingState.Succeeded, _localizer.Format("Training_Done", result.OutputDirectory ?? Target.Folder)),
                _ => (TrainingState.Failed, Failure(result)),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            (State, ResultText) = (TrainingState.Failed, _localizer.Format("Training_FailUnknown", ex.Message));
        }
        finally
        {
            _run = null;
            await run.CancelAsync();
            await gpu;
            OnPropertyChanged(nameof(StepText));

            // Nothing is left to wait for: how long it took instead.
            TimeText = _elapsed is { } took ? _localizer.Format("Training_Took", Duration(took)) : string.Empty;
        }
    }

    private string Failure(TrainingResult result) => result.Failure switch
    {
        TrainingFailure.ToolNotFound => _localizer.Format("Training_FailToolNotFound", ToolPath),
        TrainingFailure.OutOfMemory => _localizer["Training_FailOutOfMemory"],
        TrainingFailure.MissingPackage => _localizer["Training_FailMissingPackage"],
        TrainingFailure.NoGpu => _localizer["Training_FailNoGpu"],
        TrainingFailure.ModelUnavailable => _localizer["Training_FailModel"],
        TrainingFailure.DatasetError => _localizer["Training_FailDataset"],
        _ => _localizer.Format("Training_FailExit", result.ExitCode),
    };

    private void Reset()
    {
        (Step, TotalSteps, _elapsed) = (0, 0, null);
        (TimeText, EpochText, LossText, EvalLossText, LearningRateText) = (string.Empty, "–", "–", "–", "–");
        _train.Clear();
        _eval.Clear();
        (TrainPoints, EvalPoints) = ([], []);
        _log.Clear();
        LogText = string.Empty;
        (ResultText, OutputDirectory) = (null, null);
    }

    private void Apply(TrainingEvent item)
    {
        switch (item)
        {
            case TrainingStep step:
                (Step, TotalSteps, _elapsed) = (step.Step, step.TotalSteps, step.Elapsed ?? _elapsed);
                TimeText = step.Remaining is { } remaining
                    ? _localizer.Format("Training_Time", Duration(step.Elapsed ?? TimeSpan.Zero), Duration(remaining))
                    : string.Empty;
                break;
            case TrainingMetrics { IsFinal: true } final:
                if (final.Loss is { } average)
                {
                    LossText = Number(average);
                }

                break;
            case TrainingMetrics metrics:
                var x = metrics.Epoch ?? Step;
                if (metrics.Epoch is { } epoch)
                {
                    EpochText = epoch.ToString("0.##", CultureInfo.CurrentCulture);
                }

                if (metrics.Loss is { } loss && double.IsFinite(loss))
                {
                    _train.Add(new ChartPoint(x, loss));
                    TrainPoints = [.. _train];
                    LossText = Number(loss);
                }

                if (metrics.EvalLoss is { } evalLoss && double.IsFinite(evalLoss))
                {
                    _eval.Add(new ChartPoint(x, evalLoss));
                    EvalPoints = [.. _eval];
                    EvalLossText = Number(evalLoss);
                }

                if (metrics.LearningRate is { } rate)
                {
                    LearningRateText = rate.ToString("0.###e+0", CultureInfo.CurrentCulture);
                }

                break;
            case TrainingLogLine line:
                _log.Enqueue(line.Text);
                if (_log.Count > LogLines)
                {
                    _log.Dequeue();
                }

                LogText = string.Join('\n', _log);
                break;
        }
    }

    private async Task WatchGpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (await _gpu.ReadAsync(cancellationToken) is { } memory && memory.TotalMb > 0)
                {
                    GpuPercent = 100.0 * memory.UsedMb / memory.TotalMb;
                    GpuText = _localizer.Format(
                        "Training_Gpu",
                        memory.Name ?? "GPU",
                        (memory.UsedMb / 1024.0).ToString("0.0", CultureInfo.CurrentCulture),
                        (memory.TotalMb / 1024.0).ToString("0.0", CultureInfo.CurrentCulture));
                }

                await Task.Delay(GpuInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Number(double value) => value.ToString("0.0000", CultureInfo.CurrentCulture);

    private string Duration(TimeSpan time) => time.TotalHours >= 1
        ? _localizer.Format("Training_Hours", (int)time.TotalHours, time.Minutes)
        : time.TotalMinutes >= 1
            ? _localizer.Format("Training_Minutes", (int)time.TotalMinutes, time.Seconds)
            : _localizer.Format("Training_Seconds", time.Seconds);

    public void Dispose() => _run?.Cancel();
}
