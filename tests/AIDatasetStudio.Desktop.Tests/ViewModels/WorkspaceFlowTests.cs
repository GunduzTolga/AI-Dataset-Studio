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
        Assert.All(host.Dialogs.ConfirmWasDestructive, Assert.True);
    });

    [Fact]
    public Task Record_list_loads_pages_as_it_scrolls_and_filters() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetWithRecordsAsync(450);
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

        Assert.Equal(450, records.StatusFilters[0].Count);

        records.SearchText = "üçlü";
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.TotalCount == 150);
        Assert.All(records.Items, row => Assert.Equal(0, row.Ordinal % 3));
        Assert.Equal(150, records.StatusFilters[0].Count);
        Assert.Equal(150, records.StatusFilters.Single(f => f.Status == RecordStatus.Draft).Count);

        var approved = records.Items.Take(5).Select(r => r.Id).ToList();
        await host.Get<IRecordCommandService>().SetStatusAsync(approved, RecordStatus.Approved);
        records.SelectedStatusFilter = records.StatusFilters.Single(f => f.Status == RecordStatus.Approved);
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.TotalCount == 5);
        Assert.Equal(approved, records.Items.Select(r => r.Id));

        records.SearchText = "zzz-no-such-word";
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.TotalCount == 0);
        Assert.True(records.IsNoMatches);
        Assert.False(records.IsEmptyDataset);
        Assert.Equal(450, dataset.RecordCount); // filtered counts never overwrite the dataset total
    });

    [Fact]
    public Task Review_actions_update_the_list_and_counts() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetWithRecordsAsync(3);
        var records = host.Main.Records;
        var details = host.Main.Details;

        records.SelectedItem = records.Items[0];
        await DesktopTestHost.WaitUntilAsync(() => details.Record?.Id == records.Items[0].Id);
        Assert.Equal("Record #1", details.Title);
        Assert.Equal(["Instruction", "Input", "Output"], details.Fields.Select(f => f.Label));
        Assert.True(details.ShowApprove);
        Assert.False(details.ShowRestore);

        await details.ApproveCommand.ExecuteAsync(null);
        Assert.Equal(RecordStatus.Approved, records.Items[0].Status);
        Assert.Equal("Approved", records.Items[0].StatusLabel);
        Assert.False(details.ShowApprove);
        Assert.Equal(1, records.StatusFilters.Single(f => f.Status == RecordStatus.Approved).Count);
        Assert.Equal(2, records.StatusFilters.Single(f => f.Status == RecordStatus.Draft).Count);

        await details.ArchiveCommand.ExecuteAsync(null);
        Assert.True(details.ShowRestore);
        Assert.False(details.ShowArchive);

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
        var dataset = await host.CreateDatasetWithRecordsAsync(6);
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
        var dataset = await host.CreateDatasetWithRecordsAsync(0, DatasetType.Preference);
        Assert.True(host.Main.Records.IsEmptyDataset);

        host.Dialogs.ChoiceAnswer = 100;
        await host.Main.GenerateSampleDataCommand.ExecuteAsync(null);

        Assert.Equal(100, host.Main.Records.TotalCount);
        Assert.Equal(100, dataset.RecordCount);
        Assert.False(host.Main.Records.IsEmptyDataset);
        Assert.Equal("Added 100 sample records to 'Dataset'.", host.Main.Status.Message);
    });

    [Fact]
    public Task Chat_records_show_turns_and_preference_records_show_tones() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateWorkspaceAsync();
        var commands = host.Get<IRecordCommandService>();
        var explorer = host.Main.Explorer;
        host.Dialogs.TextAnswers.Enqueue("Project");
        await explorer.NewProjectCommand.ExecuteAsync(null);
        host.Dialogs.DatasetAnswer = new NewDatasetRequest("Chats", DatasetType.Chat);
        await explorer.NewDatasetCommand.ExecuteAsync(null);
        var chats = explorer.SelectedDataset!;
        host.Dialogs.DatasetAnswer = new NewDatasetRequest("Pairs", DatasetType.Preference);
        await explorer.NewDatasetCommand.ExecuteAsync(null);
        var pairs = explorer.SelectedDataset!;

        var chatId = await commands.AddAsync(chats.Id, new ChatPayload("Be brief.", [
            new ChatMessage(ChatRole.User, "Hi"),
            new ChatMessage(ChatRole.Assistant, ""),
        ]), RecordLineage.Manual());
        var pairId = await commands.AddAsync(pairs.Id, new PreferencePayload("p", "good", "bad"), RecordLineage.Manual());

        await host.Main.Details.ShowAsync(chatId);
        var details = host.Main.Details;
        Assert.True(details.IsChat);
        var system = Assert.Single(details.Fields);
        Assert.True(system.IsSystem);
        Assert.Equal("Be brief.", system.Text);
        Assert.True(details.Messages[0].IsUser);
        Assert.True(details.Messages[1].IsAssistant);
        Assert.Equal(string.Empty, details.Messages[1].Text);

        await details.ShowAsync(pairId);
        Assert.False(details.IsChat);
        Assert.Equal([FieldTone.Neutral, FieldTone.Positive, FieldTone.Negative], details.Fields.Select(f => f.Tone));
        Assert.Equal(["p", "good", "bad"], details.Fields.Select(f => f.Text));
    });

    [Fact]
    public Task Theme_choice_is_applied_and_saved() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var application = Avalonia.Application.Current!;
        var original = application.RequestedThemeVariant;
        try
        {
            Assert.True(host.Main.IsSystemTheme);

            host.Main.SetThemeCommand.Execute("Dark");

            Assert.True(host.Main.IsDarkTheme);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, application.RequestedThemeVariant);
            Assert.Equal("dark", host.Settings.UiTheme);

            host.Main.SetThemeCommand.Execute("System");
            Assert.Null(host.Settings.UiTheme);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Default, application.RequestedThemeVariant);
        }
        finally
        {
            application.RequestedThemeVariant = original;
        }
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
}
