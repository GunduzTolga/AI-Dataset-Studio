using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.Training;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Opens the training window on an exported folder (M28): from the export wizard's result, or
/// from Tools › Train a Model for a folder exported before.
/// </summary>
public sealed class TrainingLauncher(
    ITrainingRunner runner, IGpuMonitor gpu, ITrainingToolSettings tools, IFileDialogService files, IShellService shell, Localizer localizer)
{
    /// <summary>
    /// The tool a folder's configuration is for; <c>null</c> when it has none.
    /// </summary>
    public TrainingFramework? Detect(string folder) => runner.DetectFramework(folder);

    public TrainingViewModel Create(TrainingRunTarget target) => new(runner, gpu, tools, files, shell, localizer, target);
}
