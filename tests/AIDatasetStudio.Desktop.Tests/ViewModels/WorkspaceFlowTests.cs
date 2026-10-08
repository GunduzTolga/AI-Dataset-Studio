using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// End-to-end view model flows over a real workspace file, with scripted dialogs.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class WorkspaceFlowTests
{
    public WorkspaceFlowTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Projects_and_datasets_are_created_renamed_and_deleted_from_the_tree() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateWorkspaceAsync();
        var explorer = host.Main.Explorer;

        host.Dialogs.TextAnswers.Enqueue("HR Assistant");
        await explorer.NewProjectCommand.ExecuteAsync(null);
        var project = Assert.Single(explorer.Projects);
        Assert.Same(project, explorer.SelectedNode);

        host.Dialogs.DatasetAnswer = new NewDatasetRequest("Leave chats", DatasetType.Chat);
        await explorer.NewDatasetCommand.ExecuteAsync(null);
        var dataset = Assert.Single(explorer.Projects[0].Datasets);
        Assert.Equal(DatasetType.Chat, dataset.Type);
        Assert.Same(dataset, explorer.SelectedDataset);
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Records.Dataset == dataset);

        host.Dialogs.TextAnswers.Enqueue("Leave FAQ");
        await explorer.RenameCommand.ExecuteAsync(null);
        Assert.Equal("Leave FAQ", explorer.Projects[0].Datasets[0].Name);

        explorer.SelectedNode = explorer.Projects[0];
        host.Dialogs.ConfirmAnswer = false;
        await explorer.DeleteCommand.ExecuteAsync(null);
        Assert.Single(explorer.Projects);

        host.Dialogs.ConfirmAnswer = true;
        await explorer.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(explorer.Projects);
        Assert.True(explorer.IsEmpty);
        Assert.Contains("HR Assistant", host.Dialogs.ConfirmMessages[^1], StringComparison.Ordinal);
    });

    [Fact]
    public Task Record_list_loads_pages_as_it_scrolls_and_filters() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetWithRecordsAsync(host, 450);
        var records = host.Main.Records;
        records.SearchDelay = TimeSpan.Zero;

        Assert.Equal(RecordListViewModel.PageSize, records.Items.Count);
        Assert.Equal(450, records.TotalCount);
        Assert.True(records.HasMore);

        await records.LoadMoreCommand.ExecuteAsync(null);
        await records.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(450, records.Items.Count);
        Assert.False(records.HasMore);
        Assert.Equal(Enumerable.Range(1, 450).Select(i => (long)i), records.Items.Select(r => r.Ordinal));

        records.SearchText = "üçlü";
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.TotalCount == 150);
        Assert.All(records.Items, row => Assert.Equal(0, row.Ordinal % 3));

        var approved = records.Items.Take(5).Select(r => r.Id).ToList();
        await host.Get<IRecordCommandService>().SetStatusAsync(approved, RecordStatus.Approved);
        records.SelectedStatusFilter = records.StatusFilters.Single(f => f.Status == RecordStatus.Approved);
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.TotalCount == 5);
        Assert.Equal(approved, records.Items.Select(r => r.Id));

        records.SearchText = "zzz-no-such-word";
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.TotalCount == 0);
        Assert.Equal("No records match the search or filter.", records.EmptyMessage);
        Assert.Equal(450, dataset.RecordCount); // filtered counts never overwrite the dataset total
    });

    [Fact]
    public Task Review_actions_update_the_list_and_counts() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetWithRecordsAsync(host, 3);
        var records = host.Main.Records;
        var details = host.Main.Details;

        records.SelectedItem = records.Items[0];
        await DesktopTestHost.WaitUntilAsync(() => details.Record?.Id == records.Items[0].Id);
        Assert.Equal("Record #1", details.Title);
        Assert.Equal(["Instruction", "Input", "Output"], details.Fields.Select(f => f.Label));
        Assert.True(details.ApproveCommand.CanExecute(null));
        Assert.False(details.RestoreCommand.CanExecute(null));

        await details.ApproveCommand.ExecuteAsync(null);
        Assert.Equal(RecordStatus.Approved, records.Items[0].Status);
        Assert.Equal("Approved", records.Items[0].StatusLabel);
        Assert.False(details.ApproveCommand.CanExecute(null));

        await details.ArchiveCommand.ExecuteAsync(null);
        Assert.True(details.RestoreCommand.CanExecute(null));

        await details.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, records.Items.Count);
        Assert.Equal(2, records.TotalCount);
        Assert.Equal(2, dataset.RecordCount);
        Assert.False(details.HasRecord);
    });

    [Fact]
    public Task Deleting_while_searching_updates_the_dataset_total() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetWithRecordsAsync(host, 6);
        var records = host.Main.Records;
        records.SearchDelay = TimeSpan.Zero;
        records.SearchText = "üçlü";
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.TotalCount == 2);

        records.SelectedItem = records.Items[0];
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Details.Record is not null);
        await host.Main.Details.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, records.TotalCount);
        Assert.Equal(5, dataset.RecordCount);
    });

    [Fact]
    public Task Sample_data_fills_the_selected_dataset() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await CreateDatasetWithRecordsAsync(host, 0, DatasetType.Preference);
        Assert.Equal("This dataset has no records yet. Try Tools › Generate Sample Records.", host.Main.Records.EmptyMessage);

        host.Dialogs.ChoiceAnswer = 100;
        await host.Main.GenerateSampleDataCommand.ExecuteAsync(null);

        Assert.Equal(100, host.Main.Records.TotalCount);
        Assert.Equal(100, dataset.RecordCount);
        Assert.Equal("Added 100 sample records to 'Dataset'.", host.Main.Status.Message);
    });

    [Fact]
    public Task Opened_workspaces_are_remembered_and_missing_ones_forgotten() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var path = await host.CreateWorkspaceAsync("Remembered");
        Assert.Equal([path], host.Main.RecentWorkspaces.Select(r => r.FilePath));

        var missing = host.PathFor("Missing");
        host.Settings.AddRecentWorkspace(missing);
        await host.Main.OpenRecentCommand.ExecuteAsync(missing);

        Assert.Equal([path], host.Main.RecentWorkspaces.Select(r => r.FilePath));
        Assert.Equal("'Missing.adstudio' could not be found.", host.Main.Status.Message);
        Assert.True(host.Main.HasWorkspace); // the open workspace stays open
    });

    private static async Task<DatasetNodeViewModel> CreateDatasetWithRecordsAsync(
        DesktopTestHost host,
        int count,
        DatasetType type = DatasetType.Instruction)
    {
        await host.CreateWorkspaceAsync();
        var explorer = host.Main.Explorer;
        host.Dialogs.TextAnswers.Enqueue("Project");
        await explorer.NewProjectCommand.ExecuteAsync(null);
        host.Dialogs.DatasetAnswer = new NewDatasetRequest("Dataset", type);
        await explorer.NewDatasetCommand.ExecuteAsync(null);
        var dataset = explorer.SelectedDataset!;

        if (count > 0)
        {
            await host.Get<IRecordCommandService>().AddRangeAsync(
                dataset.Id,
                Enumerable.Range(1, count).Select(i => new NewRecord(
                    new InstructionPayload($"Soru {i}", "", i % 3 == 0 ? "üçlü" : "diğer"),
                    RecordLineage.Manual())));
        }

        await host.Main.Records.ReloadAsync();
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Records.Dataset == dataset && !host.Main.Records.IsLoading);
        return dataset;
    }
}
