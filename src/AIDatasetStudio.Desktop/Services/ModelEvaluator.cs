using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Opens Tools › Evaluate a Model for a dataset (M22) with what it starts from: the options of the
/// dataset's last export, whose split tells the questions a trained model has not seen, and the
/// dataset's earlier evaluations.
/// </summary>
public sealed class ModelEvaluator(
    AIAssistant ai, IRecordQueryService queries, IExportService export, IEvaluationRunService runs, IFileDialogService files, IDialogService dialogs, Localizer localizer)
{
    internal AIAssistant AI => ai;

    internal IRecordQueryService Queries => queries;

    internal IExportService Export => export;

    internal IEvaluationRunService Runs => runs;

    internal IFileDialogService Files => files;

    internal IDialogService Dialogs => dialogs;

    internal Localizer Localizer => localizer;

    public async Task<ModelEvaluationViewModel> CreateAsync(Guid datasetId, string name, DatasetType type, CancellationToken cancellationToken = default)
    {
        var target = new EvaluationTarget(datasetId, name, type, await export.GetLastOptionsAsync(datasetId, cancellationToken));
        return new ModelEvaluationViewModel(this, target, await runs.GetRunsAsync(datasetId, cancellationToken));
    }
}
