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
