namespace AIDatasetStudio.Application.Training;

/// <summary>
/// Something a training run reported (M28).
/// </summary>
public abstract record TrainingEvent;

/// <summary>
/// The optimizer has taken <paramref name="Step"/> of <paramref name="TotalSteps"/> steps.
/// </summary>
/// <param name="Elapsed">Time since training started, as the tool counts it.</param>
/// <param name="Remaining">The tool's estimate of the time left; <c>null</c> until it has one.</param>
public sealed record TrainingStep(int Step, int TotalSteps, TimeSpan? Elapsed, TimeSpan? Remaining) : TrainingEvent;

/// <summary>
/// Values the trainer logged: the training loss every few steps, the validation loss after an
/// evaluation, and the final averages.
/// </summary>
public sealed record TrainingMetrics(double? Epoch, double? Loss, double? EvalLoss, double? LearningRate, bool IsFinal = false) : TrainingEvent;

/// <summary>
/// A line the tool wrote, for the log view.
/// </summary>
public sealed record TrainingLogLine(string Text) : TrainingEvent;

/// <summary>
/// The stream a piece of a training tool's output came from: each finishes its lines on its own.
/// </summary>
public enum TrainingOutputStream
{
    StandardOutput,

    StandardError,
}

/// <summary>
/// Why a training run stopped, as far as its output tells.
/// </summary>
public enum TrainingFailure
{
    /// <summary>The output names no known cause; the log has the details.</summary>
    Unknown,

    /// <summary>The tool's command could not be started.</summary>
    ToolNotFound,

    /// <summary>The GPU ran out of memory.</summary>
    OutOfMemory,

    /// <summary>A Python package the tool needs is missing.</summary>
    MissingPackage,

    /// <summary>No GPU the settings need was found (16-bit training on a computer without one).</summary>
    NoGpu,

    /// <summary>The base model could not be found or downloaded, or access to it is restricted.</summary>
    ModelUnavailable,

    /// <summary>The tool could not read the data files.</summary>
    DatasetError,
}

/// <summary>
/// How a training run ended.
/// </summary>
/// <param name="OutputDirectory">Where the tool saves the adapter (the configuration's output folder).</param>
/// <param name="LastLines">The last lines of output, to show when the run failed.</param>
public sealed record TrainingResult(int ExitCode, bool Cancelled, string? OutputDirectory, TrainingFailure? Failure, IReadOnlyList<string> LastLines)
{
    public bool Succeeded => !Cancelled && ExitCode == 0;
}
