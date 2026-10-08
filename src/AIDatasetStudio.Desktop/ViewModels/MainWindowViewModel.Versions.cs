using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.ViewModels.Versions;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// Dataset versions (SDD §14): freeze, compare, go back, export.
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Freezes the selected dataset as a new version.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task CreateVersionAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset || !await Details.SaveIfDirtyAsync())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var existing = await _versions.ListAsync(dataset.Id);
            var progress = new Progress<int>(done => Status.Set("Status_VersionAnalyzing", done));
            var target = new VersionsTarget(dataset.Id, dataset.Name, dataset.Type, dataset.RecordCount);
            if (await VersionsViewModel.PromptAndCreateAsync(_versions, _dialogs, _localizer, target, existing, progress) is { } created)
            {
                dataset.LatestVersion = created.Number;
                Status.Set("Status_VersionCreated", created.Number, created.RecordCount);
            }
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Shows the versions of the selected dataset: compare, export, go back, delete.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task VersionsAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset || !await Details.SaveIfDirtyAsync())
        {
            return;
        }

        var versions = new VersionsViewModel(
            _versions,
            _operations,
            _dialogs,
            _localizer,
            _tags,
            new VersionsTarget(dataset.Id, dataset.Name, dataset.Type, dataset.RecordCount),
            async version => await ExportDatasetAsync(dataset, await _versions.ListAsync(dataset.Id), version.Id));
        await _dialogs.ShowVersionsAsync(versions);
        if (!versions.HasChangedData)
        {
            return;
        }

        // Versions were made or deleted, or the records went back to a version.
        IsBusy = true;
        try
        {
            dataset.LatestVersion = (await _versions.ListAsync(dataset.Id)).FirstOrDefault()?.Number;
            Explorer.SetRecordCount(dataset.Id, await _queries.CountAsync(dataset.Id, RecordFilter.None));
            if (Records.Dataset?.Id == dataset.Id)
            {
                await Records.ReloadAsync();
                Overview.StartAnalysis();
            }
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
