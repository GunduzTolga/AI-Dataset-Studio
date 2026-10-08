using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// AI connections and writing with a model (SDD §17).
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Tools › AI Connections: the model servers that help write records. They belong to this
    /// computer, so no workspace is needed.
    /// </summary>
    [RelayCommand]
    private Task ManageAIConnectionsAsync() => _dialogs.ShowAIConnectionsAsync(_ai.CreateConnections());

    /// <summary>
    /// File › Extract Q&amp;A from Document: picks a document and has the model write the questions
    /// it answers into the selected dataset (SDD §17.5).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ExtractQuestionsAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset)
        {
            return;
        }

        if (await _files.PickDocumentAsync(_questions.SupportedExtensions) is { } path)
        {
            await ExtractQuestionsAsync(dataset, path);
        }
    }

    private bool CanGenerateVariations() =>
        CanReview() && Records.SelectedRows.Count > 0 && Records.Dataset is { } dataset && Variations.Supports(dataset.Type);

    /// <summary>
    /// Record › Generate Variations (Mod+Shift+G): the shown or selected records asked in other
    /// words by the model, added as records of their families once reviewed (SDD §17.6).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateVariations))]
    private async Task GenerateVariationsAsync()
    {
        if (Records.Dataset is not { } dataset || Records.SelectedRows.Select(r => r.Id).ToList() is not { Count: > 0 } ids)
        {
            return;
        }

        // The variations are written from the saved record, and added after the edit in progress.
        if (!await Details.SaveIfDirtyAsync())
        {
            return;
        }

        VariationsViewModel variations;
        try
        {
            variations = await _variations.CreateAsync(new QATarget(dataset.Id, dataset.Name, dataset.Type, dataset.AIGuidance), ids);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
            return;
        }

        variations.AIConnectionsRequested += (_, _) => ManageAIConnectionsCommand.Execute(null);
        await _dialogs.ShowVariationsAsync(variations);
        if (variations.AddedIds is not { Count: > 0 } added)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await ShowAffectedAsync(dataset.Id, added);
            Status.Set("Status_VariationsAdded", added.Count, dataset.Name);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExtractQuestionsAsync(DatasetNodeViewModel dataset, string path)
    {
        // The added records are a step in the history after the edit in progress.
        if (!await Details.SaveIfDirtyAsync())
        {
            return;
        }

        var extraction = _questions.Create(path, new QATarget(dataset.Id, dataset.Name, dataset.Type, dataset.AIGuidance));
        extraction.AIConnectionsRequested += (_, _) => ManageAIConnectionsCommand.Execute(null);
        await _dialogs.ShowQAExtractionAsync(extraction);
        if (extraction.AddedIds is not { Count: > 0 } added)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await ShowAffectedAsync(dataset.Id, added);
            Status.Set("Status_QAAdded", added.Count, dataset.Name);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }
}
