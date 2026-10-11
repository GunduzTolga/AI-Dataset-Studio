using AIDatasetStudio.Application.Export;

namespace AIDatasetStudio.Application.Training;

/// <summary>
/// A training run to start: the folder an export wrote, with its data and configuration.
/// </summary>
/// <param name="ToolPath">The tool's command (<c>llamafactory-cli</c> or <c>axolotl</c>), as a path or a name on PATH.</param>
public sealed record TrainingRequest(TrainingFramework Framework, string Folder, string ToolPath);

/// <summary>
/// Runs a fine-tuning tool installed on this computer on an exported folder and reports its
/// progress (M28, ADR-048).
/// </summary>
public interface ITrainingRunner
{
    /// <summary>
    /// The tool's command: the one the user chose, or the first on PATH; <c>null</c> when neither exists.
    /// </summary>
    string? FindTool(TrainingFramework framework);

    /// <summary>
    /// The tool a folder is set up for, from the configuration files in it; <c>null</c> when it has none.
    /// </summary>
    TrainingFramework? DetectFramework(string folder);

    /// <summary>
    /// Runs the tool in the folder until it exits. Cancelling stops the tool and everything it started.
    /// </summary>
    Task<TrainingResult> RunAsync(TrainingRequest request, IProgress<TrainingEvent> progress, CancellationToken cancellationToken = default);
}

/// <summary>
/// The training tools the user pointed the app to, kept with the other preferences.
/// </summary>
public interface ITrainingToolSettings
{
    string? GetToolPath(TrainingFramework framework);

    void SetToolPath(TrainingFramework framework, string? path);
}

/// <summary>
/// How much of the GPU's memory is in use.
/// </summary>
public sealed record GpuMemory(string? Name, long UsedMb, long TotalMb);

/// <summary>
/// Reads the GPU's memory use from the vendor's tool (<c>nvidia-smi</c>, <c>rocm-smi</c>).
/// </summary>
public interface IGpuMonitor
{
    /// <summary>
    /// The first GPU's memory use; <c>null</c> when no vendor tool answers.
    /// </summary>
    Task<GpuMemory?> ReadAsync(CancellationToken cancellationToken = default);
}
