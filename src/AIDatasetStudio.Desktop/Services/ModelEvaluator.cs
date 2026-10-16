using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Opens Tools › Evaluate a Model for a dataset (M22) with what it starts from: the options of the
/// dataset's last export, whose split tells the questions a trained model has not seen, the
/// dataset's earlier evaluations, and the adapter a training saved in the export's folder (M30).
/// </summary>
public sealed class ModelEvaluator(
    AIAssistant ai,
    IRecordQueryService queries,
    IExportService export,
    IEvaluationRunService runs,
    IFileDialogService files,
    IDialogService dialogs,
    IModelServer server,
    ITrainingRunner training,
    Localizer localizer)
{
    internal AIAssistant AI => ai;

    internal IRecordQueryService Queries => queries;

    internal IExportService Export => export;

    internal IEvaluationRunService Runs => runs;

    internal IFileDialogService Files => files;

    internal IDialogService Dialogs => dialogs;

    internal Localizer Localizer => localizer;

    internal IModelServer Server => server;

    /// <summary>
    /// LLaMA-Factory's command, which also serves the adapters it trained; <c>null</c> when it is not found.
    /// </summary>
    internal string? FindServerTool() => training.FindTool(TrainingFramework.LlamaFactory);

    /// <param name="trainedFolder">A folder just trained in, whose adapter is the model chosen to
    /// evaluate; otherwise the folder of the dataset's last export is looked at.</param>
    public async Task<ModelEvaluationViewModel> CreateAsync(
        Guid datasetId, string name, DatasetType type, string? trainedFolder = null, CancellationToken cancellationToken = default)
    {
        var target = new EvaluationTarget(datasetId, name, type, await export.GetLastOptionsAsync(datasetId, cancellationToken));
        var folder = trainedFolder;
        if (folder is null && await export.GetLastExportAsync(datasetId, cancellationToken) is { Files: [var file, ..] })
        {
            folder = Path.GetDirectoryName(file);
        }

        var adapter = folder is null ? null : server.FindAdapter(folder);
        return new ModelEvaluationViewModel(this, target, await runs.GetRunsAsync(datasetId, cancellationToken), adapter, chooseAdapter: trainedFolder is not null);
    }
}
