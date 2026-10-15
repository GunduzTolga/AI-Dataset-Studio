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
/// Editing, speed review, batch actions and undo/redo (M3) over a real workspace.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ReviewFlowTests
{
    public ReviewFlowTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Edits_are_saved_to_review_and_can_be_undone_and_redone() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetWithRecordsAsync(3);
        var (records, details) = (host.Main.Records, host.Main.Details);
        await SelectAsync(host, 0);
        await details.ApproveCommand.ExecuteAsync(null);

        details.Fields[0].Text = "Soru 1 (düzeltildi)";
        Assert.True(details.IsDirty);
        Assert.True(details.SaveCommand.CanExecute(null));
        details.Fields[0].Text = "Soru 1";
        Assert.False(details.IsDirty); // back to the saved text

        details.Fields[0].Text = "Soru 1 (düzeltildi)";
        await details.SaveCommand.ExecuteAsync(null);

        Assert.False(details.IsDirty);
        Assert.Equal(RecordStatus.InReview, details.Status); // an edited approved record goes back to review
        Assert.Equal("Soru 1 (düzeltildi)", records.Items[0].Preview);
        Assert.Equal(RecordStatus.InReview, records.Items[0].Status);
        Assert.Equal("Saved record #1; it is now in review.", host.Main.Status.Message);
        Assert.Equal("_Undo Edit #1", host.Main.UndoHeader);

        await host.Main.UndoCommand.ExecuteAsync(null);
        await host.WaitForDetailsAsync(records.Items[0].Id);
        Assert.Equal("Soru 1", details.Fields[0].Text);
        Assert.Equal(RecordStatus.Approved, details.Status);
        Assert.Equal("Undone: Edit #1", host.Main.Status.Message);
        Assert.Equal("_Undo Approve #1", host.Main.UndoHeader);
        Assert.Equal("_Redo Edit #1", host.Main.RedoHeader);

        await host.Main.RedoCommand.ExecuteAsync(null);
        await host.WaitForDetailsAsync(records.Items[0].Id);
        Assert.Equal("Soru 1 (düzeltildi)", details.Fields[0].Text);
        Assert.Equal(RecordStatus.InReview, details.Status);
        Assert.False(host.Main.RedoCommand.CanExecute(null));
        Assert.Equal(3, dataset.RecordCount);
    });

    [Fact]
    public Task Moving_to_another_record_saves_pending_edits_and_discard_drops_them() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var (records, details) = (host.Main.Records, host.Main.Details);
        await SelectAsync(host, 0);

        details.Fields[3].Text = "yeni cevap";
        await host.Main.NextRecordCommand.ExecuteAsync(null);
        await host.WaitForDetailsAsync(records.Items[1].Id);

        var saved = await host.Get<IRecordQueryService>().GetAsync(records.Items[0].Id);
        Assert.Equal(new InstructionPayload("Soru 1", "", "yeni cevap"), saved!.Payload);
        Assert.Equal(RecordStatus.InReview, records.Items[0].Status);

        details.Fields[3].Text = "vazgeçilecek";
        details.DiscardCommand.Execute(null);
        Assert.False(details.IsDirty);
        Assert.Equal("diğer", details.Fields[3].Text);
    });

    [Fact]
    public Task Chat_turns_can_be_added_switched_and_removed() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetWithRecordsAsync(0, DatasetType.Chat);
        var id = await host.Get<IRecordCommandService>().AddAsync(
            dataset.Id,
            new ChatPayload(null, [new ChatMessage(ChatRole.User, "Merhaba"), new ChatMessage(ChatRole.Assistant, "Selam")]),
            RecordLineage.Manual());
        await host.Main.Records.ReloadAsync();
        await SelectAsync(host, 0);
        var details = host.Main.Details;

        details.AddMessageCommand.Execute(null);
        Assert.Equal(ChatRole.User, details.Messages[2].Role); // alternates after the assistant turn
        details.Messages[2].Text = "Nasılsın?";
        details.AddMessageCommand.Execute(null);
        details.Messages[3].Text = "İyiyim.";
        details.Messages[1].SwitchRoleCommand.Execute(null);
        details.Messages[1].SwitchRoleCommand.Execute(null);
        details.Messages[0].RemoveCommand.Execute(null);
        details.Fields[0].Text = "Kısa yanıt ver.";
        await details.SaveCommand.ExecuteAsync(null);

        var saved = await host.Get<IRecordQueryService>().GetAsync(id);
        Assert.Equal(
            new ChatPayload("Kısa yanıt ver.", [
                new ChatMessage(ChatRole.Assistant, "Selam"),
                new ChatMessage(ChatRole.User, "Nasılsın?"),
                new ChatMessage(ChatRole.Assistant, "İyiyim."),
            ]),
            saved!.Payload);
    });

    [Fact]
    public Task Archived_records_are_read_only() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(1);
        var details = host.Main.Details;
        await SelectAsync(host, 0);

        await details.ArchiveCommand.ExecuteAsync(null);

        Assert.True(details.IsArchived);
        Assert.False(details.IsEditable);
        details.Fields[0].Text = "değişiklik";
        Assert.False(details.SaveCommand.CanExecute(null));
        Assert.True(await details.SaveIfDirtyAsync());
        var saved = await host.Get<IRecordQueryService>().GetAsync(details.Record!.Id);
        Assert.Equal("Soru 1", ((InstructionPayload)saved!.Payload).Instruction);
    });

    [Fact]
    public Task Approve_and_next_reviews_records_in_a_row() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var (main, records, details) = (host.Main, host.Main.Records, host.Main.Details);
        await SelectAsync(host, 0);

        await main.ApproveAndNextCommand.ExecuteAsync(null);
        await host.WaitForDetailsAsync(records.Items[1].Id);

        // Pending edits are saved first, then the record is approved.
        details.Fields[3].Text = "düzeltilmiş";
        await main.ApproveAndNextCommand.ExecuteAsync(null);
        await host.WaitForDetailsAsync(records.Items[2].Id);

        await main.ArchiveAndNextCommand.ExecuteAsync(null); // the last record: archived, selection stays

        Assert.Equal([RecordStatus.Approved, RecordStatus.Approved, RecordStatus.Archived], records.Items.Select(r => r.Status));
        var second = await host.Get<IRecordQueryService>().GetAsync(records.Items[1].Id);
        Assert.Equal("düzeltilmiş", ((InstructionPayload)second!.Payload).Output);
        Assert.Same(records.Items[2], records.SelectedItem);
        Assert.Equal(2, records.StatusFilters.Single(f => f.Status == RecordStatus.Approved).Count);

        main.PreviousRecordCommand.Execute(null);
        Assert.Same(records.Items[1], records.SelectedItem);
    });

    [Fact]
    public Task Next_loads_the_following_page_at_the_end_of_the_list() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(RecordListViewModel.PageSize + 5);
        var records = host.Main.Records;
        records.SelectedItem = records.Items[^1];

        await host.Main.NextRecordCommand.ExecuteAsync(null);

        Assert.Equal(RecordListViewModel.PageSize + 5, records.Items.Count);
        Assert.Equal(RecordListViewModel.PageSize + 1, records.SelectedItem!.Ordinal);
    });

    [Fact]
    public Task Batch_actions_apply_to_the_selection_and_undo_in_one_step() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(4);
        var (main, records, details) = (host.Main, host.Main.Records, host.Main.Details);
        await SelectAsync(host, 3);
        await details.ArchiveCommand.ExecuteAsync(null);

        records.Select([records.Items[0].Id, records.Items[1].Id, records.Items[3].Id]);
        await DesktopTestHost.WaitUntilAsync(() => details.IsBatch);

        Assert.Equal("3 records selected", details.BatchTitle);
        Assert.Equal([(RecordStatus.Draft, 2), (RecordStatus.Archived, 1)], details.BatchCounts.Select(c => (c.Status, c.Count)));
        Assert.True(details.ShowBatchApprove);
        Assert.True(details.ShowBatchRestore);

        await details.BatchApproveCommand.ExecuteAsync(null);

        Assert.Equal([RecordStatus.Approved, RecordStatus.Approved, RecordStatus.Draft, RecordStatus.Archived], records.Items.Select(r => r.Status));
        Assert.Equal("2 records set to Approved; 1 skipped.", main.Status.Message);
        Assert.Equal("_Undo Approve 2 records", main.UndoHeader);

        await main.UndoCommand.ExecuteAsync(null);

        Assert.Equal([RecordStatus.Draft, RecordStatus.Draft, RecordStatus.Draft, RecordStatus.Archived], records.Items.Select(r => r.Status));
        Assert.Equal(2, records.SelectionCount); // the records the step changed are selected again
        Assert.Equal("Undone: Approve 2 records", main.Status.Message);
    });

    [Fact]
    public Task Deleted_records_come_back_with_undo() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetWithRecordsAsync(5);
        var (main, records, details) = (host.Main, host.Main.Records, host.Main.Details);
        var ids = records.Items.Skip(1).Take(3).Select(r => r.Id).ToList();
        records.Select(ids);
        await DesktopTestHost.WaitUntilAsync(() => details.IsBatch);

        await main.DeleteSelectionCommand.ExecuteAsync(null);

        Assert.Contains("3 records", host.Dialogs.ConfirmMessages[^1], StringComparison.Ordinal);
        Assert.Equal([1L, 5L], records.Items.Select(r => r.Ordinal));
        Assert.Equal(2, dataset.RecordCount);

        await main.UndoCommand.ExecuteAsync(null);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], records.Items.Select(r => r.Ordinal));
        Assert.Equal(5, dataset.RecordCount);
        Assert.Equal(ids, records.SelectedRows.Select(r => r.Id));
        Assert.Equal(1, await host.Get<IRecordQueryService>().CountAsync(dataset.Id, new RecordFilter(SearchText: "Soru 3")));

        await main.RedoCommand.ExecuteAsync(null);
        Assert.Equal(2, records.Items.Count);
        Assert.Equal(2, dataset.RecordCount);
    });

    [Fact]
    public Task Undo_shows_the_dataset_it_changed() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var first = await host.CreateDatasetWithRecordsAsync(2);
        var (main, records) = (host.Main, host.Main.Records);
        await SelectAsync(host, 1);
        await main.ApproveAndNextCommand.ExecuteAsync(null);
        host.Dialogs.DatasetAnswer = new NewDatasetRequest("Other", DatasetType.Chat);
        await main.Explorer.NewDatasetCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => records.Dataset?.Name == "Other" && !records.IsLoading);

        await main.UndoCommand.ExecuteAsync(null);

        Assert.Equal(first.Id, records.Dataset?.Id);
        Assert.Equal(RecordStatus.Draft, records.Items[1].Status);
        Assert.Same(records.Items[1], records.SelectedItem);
    });

    [Fact]
    public Task Steps_whose_records_are_gone_are_dropped() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(1);
        var main = host.Main;
        await SelectAsync(host, 0);
        await main.Details.ApproveCommand.ExecuteAsync(null);
        await host.Get<IRecordCommandService>().DeleteAsync([main.Records.Items[0].Id]); // behind the history's back

        await main.UndoCommand.ExecuteAsync(null);

        Assert.Equal("The records or tag of that step no longer exist; it was removed from the history.", main.Status.Message);
        Assert.False(main.UndoCommand.CanExecute(null));
        Assert.False(main.RedoCommand.CanExecute(null));
    });

    [Fact]
    public Task Closing_the_workspace_clears_the_history() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(1);
        await SelectAsync(host, 0);
        await host.Main.Details.ApproveCommand.ExecuteAsync(null);
        Assert.True(host.Main.UndoCommand.CanExecute(null));

        await host.Main.CloseWorkspaceCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.HasWorkspace);

        Assert.False(host.Get<UndoHistory>().CanUndo);
        Assert.Equal("_Undo", host.Main.UndoHeader);
    });

    private static async Task SelectAsync(DesktopTestHost host, int index)
    {
        var row = host.Main.Records.Items[index];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
    }
}
