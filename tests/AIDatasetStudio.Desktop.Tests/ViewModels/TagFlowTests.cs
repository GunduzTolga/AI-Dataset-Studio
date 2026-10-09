using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Tags (M9): adding them in the editor and with Mod+1…5, on one or many records, filtering by
/// them, managing them, splitting exports by them and seeing them in version comparisons.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TagFlowTests
{
    public TagFlowTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task A_tag_is_created_and_added_from_the_editor_and_can_be_undone() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var (records, details) = (host.Main.Records, host.Main.Details);
        await SelectAsync(host, 0);

        details.OpenTagPickerCommand.Execute(null);
        var picker = details.TagPicker;
        Assert.True(picker.IsOpen);
        Assert.True(picker.IsEmpty); // no tags yet: the hint says how to make one

        picker.Query = "kategori:hukuk";
        Assert.True(picker.CanCreate);
        Assert.Equal("Create “kategori:hukuk”", picker.CreateText);
        await picker.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(["kategori:hukuk"], details.RecordTags.Select(t => t.Name));
        Assert.Equal(["kategori:hukuk"], records.Items[0].Tags.Select(t => t.Name));
        Assert.Empty(records.Items[1].Tags);
        Assert.Equal("Record #1: tagged “kategori:hukuk”.", host.Main.Status.Message);
        Assert.Equal(1, host.Main.Tags.Tags.Single().Shortcut); // the first tag takes Mod+1
        Assert.True(picker.IsOpen); // stays open for the next tag
        Assert.Equal(string.Empty, picker.Query);
        Assert.Empty(picker.Matches); // the record already has the only tag

        // An existing tag is found by any casing and not created twice.
        await SelectAsync(host, 1);
        details.OpenTagPickerCommand.Execute(null);
        picker.Query = "KATEGORİ:HUKUK";
        Assert.False(picker.CanCreate);
        Assert.Equal(["kategori:hukuk"], picker.Matches.Select(t => t.Name));
        await picker.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(await host.Get<ITagService>().ListAsync(cancellationToken: Ct));
        Assert.Equal(["kategori:hukuk"], records.Items[1].Tags.Select(t => t.Name));
        Assert.Equal("_Undo Add “kategori:hukuk” to #2", host.Main.UndoHeader);

        await host.Main.UndoCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => records.Items.Count == 3 && records.Items[1].Tags.Count == 0);
        Assert.Equal("Undone: Add “kategori:hukuk” to #2", host.Main.Status.Message);
        await host.WaitForDetailsAsync(records.Items[1].Id);
        Assert.Empty(details.RecordTags);

        await host.Main.RedoCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => records.Items.Count == 3 && records.Items[1].Tags.Count == 1);

        // The chip's × takes it off again.
        await host.WaitForDetailsAsync(records.Items[1].Id);
        await details.RemoveTagCommand.ExecuteAsync(details.RecordTags[0]);
        Assert.Empty(details.RecordTags);
        Assert.Empty(records.Items[1].Tags);
        Assert.Equal("Record #2: “kategori:hukuk” removed.", host.Main.Status.Message);
    });

    [Fact]
    public Task Mod_number_toggles_a_tag_on_one_record_or_on_every_selected_record() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var (records, details) = (host.Main.Records, host.Main.Details);
        var tags = host.Get<ITagService>();
        await tags.CreateAsync("hukuk", Ct);
        await tags.CreateAsync("teknik", Ct);
        await host.Main.Tags.ReloadAsync();
        Assert.Equal(["Ctrl+1", "Ctrl+2"], host.Main.Tags.WithShortcuts.Select(t => t.ShortcutText));

        await SelectAsync(host, 0);
        await host.Main.ToggleTagSlotCommand.ExecuteAsync(1);
        await host.Main.ToggleTagSlotCommand.ExecuteAsync(2);
        Assert.Equal(["hukuk", "teknik"], details.RecordTags.Select(t => t.Name));
        Assert.Equal(["hukuk", "teknik"], records.Items[0].Tags.Select(t => t.Name));

        await host.Main.ToggleTagSlotCommand.ExecuteAsync(1);
        Assert.Equal(["teknik"], details.RecordTags.Select(t => t.Name));
        Assert.Equal("Record #1: “hukuk” removed.", host.Main.Status.Message);

        await host.Main.ToggleTagSlotCommand.ExecuteAsync(4);
        Assert.Equal("No tag uses Ctrl+4. Give tags shortcuts in Tools › Tags.", host.Main.Status.Message);

        // Several records: added to all unless all have it, then taken off all.
        await SelectAsync(host, 1);
        await host.Main.ToggleTagSlotCommand.ExecuteAsync(1);
        records.Select([.. records.Items.Select(r => r.Id)]);
        await DesktopTestHost.WaitUntilAsync(() => details.IsBatch);
        Assert.Equal([("hukuk", "1"), ("teknik", "1")], details.BatchTags.Select(t => (t.Tag.Name, t.CountText)));

        await host.Main.ToggleTagSlotCommand.ExecuteAsync(1);
        Assert.Equal("“hukuk” added to 2 records.", host.Main.Status.Message);
        Assert.All(records.Items, row => Assert.Contains("hukuk", row.Tags.Select(t => t.Name)));
        Assert.Equal([("hukuk", "3"), ("teknik", "1")], details.BatchTags.Select(t => (t.Tag.Name, t.CountText)));

        await host.Main.ToggleTagSlotCommand.ExecuteAsync(1);
        Assert.Equal("“hukuk” removed from 3 records.", host.Main.Status.Message);
        Assert.All(records.Items, row => Assert.DoesNotContain("hukuk", row.Tags.Select(t => t.Name)));

        // The batch picker leaves out only the tags every selected record has.
        details.OpenBatchTagPickerCommand.Execute(null);
        Assert.Equal(["hukuk", "teknik"], details.BatchTagPicker.Matches.Select(t => t.Name));
        await details.BatchTagPicker.PickCommand.ExecuteAsync(details.BatchTagPicker.Matches[1]);
        Assert.All(records.Items, row => Assert.Contains("teknik", row.Tags.Select(t => t.Name)));
        Assert.DoesNotContain(details.BatchTagPicker.Matches, t => t.Name == "teknik");

        await details.RemoveTagCommand.ExecuteAsync(details.BatchTags.Single(t => t.Tag.Name == "teknik").Tag);
        Assert.Empty(details.BatchTags);

        // One step each in the history.
        await host.Main.UndoCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => records.Items.Count == 3 && records.Items.All(row => row.Tags.Any(t => t.Name == "teknik")));
    });

    [Fact]
    public Task The_list_filters_by_tag_and_shows_records_without_tags() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(5);
        var records = host.Main.Records;
        var ids = records.Items.Select(r => r.Id).ToList();
        var tags = host.Get<ITagService>();
        var hukuk = await tags.CreateAsync("hukuk", Ct);
        var teknik = await tags.CreateAsync("teknik", Ct);
        var commands = host.Get<IRecordCommandService>();
        await commands.SetTagAsync([ids[0], ids[2]], hukuk.Id, present: true, Ct);
        await commands.SetTagAsync([ids[2], ids[3]], teknik.Id, present: true, Ct);
        await commands.SetStatusAsync([ids[2]], RecordStatus.Approved, Ct);
        await host.Main.Tags.ReloadAsync();
        await records.ReloadAsync();

        Assert.Equal(["All tags", "Untagged", "hukuk", "teknik"], records.TagFilters.Select(f => f.Label));
        Assert.Equal(["hukuk", "teknik"], records.Items[2].Tags.Select(t => t.Name));

        records.SelectedTagFilter = records.TagFilters[2];
        await records.Loading;
        Assert.Equal([1L, 3L], records.Items.Select(r => r.Ordinal));
        Assert.Equal([2L, 1L], records.StatusFilters.Take(2).Select(f => f.Count)); // all, draft
        Assert.Equal(5, records.Dataset!.RecordCount); // a filtered count is not the dataset's

        records.SelectedTagFilter = records.TagFilters[1];
        await records.Loading;
        Assert.Equal([2L, 5L], records.Items.Select(r => r.Ordinal));

        records.SearchText = "Soru 5";
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.Items.Count == 1);
        records.SearchText = string.Empty;
        await DesktopTestHost.WaitUntilAsync(() => !records.IsLoading && records.Items.Count == 2);

        // Deleting the filtered tag falls back to all records.
        records.ShowTag(teknik.Id);
        await records.Loading;
        Assert.Equal([3L, 4L], records.Items.Select(r => r.Ordinal));
        await tags.DeleteAsync(teknik.Id, Ct);
        await host.Main.Tags.ReloadAsync();
        await records.Loading;
        Assert.Equal(records.TagFilters[0], records.SelectedTagFilter);
        Assert.Equal(5, records.Items.Count);
        Assert.Equal(["hukuk"], records.Items[2].Tags.Select(t => t.Name));
    });

    [Fact]
    public Task The_tag_manager_renames_recolors_moves_shortcuts_and_deletes() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var records = host.Main.Records;
        var tags = host.Get<ITagService>();
        var first = await tags.CreateAsync("hukuk", Ct);
        await tags.CreateAsync("teknik", Ct);
        await host.Get<IRecordCommandService>().SetTagAsync([records.Items[0].Id, records.Items[1].Id], first.Id, present: true, Ct);
        await host.Main.Tags.ReloadAsync();
        await records.ReloadAsync();

        host.Dialogs.TagManagerScript = async manager =>
        {
            await manager.InitializeAsync();
            Assert.Equal([("hukuk", "2 records", 1), ("teknik", "0 records", 2)], manager.Items.Select(r => (r.Name, r.CountText, r.SelectedShortcut.Slot)));
            Assert.Equal(["No shortcut", "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5"], manager.ShortcutOptions.Select(o => o.Label));

            // Mod+1 moves to "teknik"; "hukuk" loses it.
            manager.Items[1].SelectedShortcut = manager.ShortcutOptions[1];
            await DesktopTestHost.WaitUntilAsync(() => manager.Items[0].SelectedShortcut.Slot is null && manager.Items[1].SelectedShortcut.Slot == 1);

            host.Dialogs.TextAnswers.Enqueue("kategori:hukuk");
            await manager.Items[0].RenameCommand.ExecuteAsync(null);
            Assert.Equal(["kategori:hukuk", "teknik"], manager.Items.Select(r => r.Name));
            Assert.Equal("kategori", manager.Items[0].Group);

            await manager.Items[1].SetColorCommand.ExecuteAsync("#EF4444");
            Assert.Equal("#EF4444", manager.Items[1].Tag.Info.ColorHex);

            manager.NewName = "TEKNİK";
            await manager.AddCommand.ExecuteAsync(null);
            Assert.Equal("The operation could not be completed: A tag named 'TEKNİK' already exists.", manager.ErrorMessage);
            manager.NewName = "veri";
            await manager.AddCommand.ExecuteAsync(null);
            Assert.Null(manager.ErrorMessage);
            Assert.Equal("“veri” created.", manager.Notice);
            Assert.Equal(["kategori:hukuk", "teknik", "veri"], manager.Items.Select(r => r.Name));

            await manager.Items[0].DeleteCommand.ExecuteAsync(null);
            Assert.Equal(
                "Delete “kategori:hukuk”? It is removed from 2 records, also as they are in versions. This cannot be undone.",
                host.Dialogs.ConfirmMessages[^1]);
            Assert.True(host.Dialogs.ConfirmWasDestructive[^1]);
            Assert.Equal("“kategori:hukuk” deleted and removed from 2 records.", manager.Notice);
            Assert.Equal(["teknik", "veri"], manager.Items.Select(r => r.Name));

            manager.Items[0].ShowRecordsCommand.Execute(null);
        };

        // The list follows the catalog while the window is open.
        await host.Main.ManageTagsCommand.ExecuteAsync(null);
        await records.Loading;

        Assert.Equal(["teknik"], records.SelectedTagFilter?.Tag?.Name is { } name ? [name] : Array.Empty<string>());
        Assert.Empty(records.Items); // nothing has "teknik"
        Assert.Equal(["All tags", "Untagged", "teknik", "veri"], records.TagFilters.Select(f => f.Label));
        Assert.Equal(1, host.Main.Tags.FindByName("teknik")!.Shortcut);

        records.SelectedTagFilter = records.TagFilters[0];
        await records.Loading;
        Assert.All(records.Items, row => Assert.Empty(row.Tags));
    });

    [Fact]
    public Task Undoing_a_tag_that_was_deleted_since_is_dropped() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(2);
        var tag = await host.Get<ITagService>().CreateAsync("geçici", Ct);
        await host.Main.Tags.ReloadAsync();
        await SelectAsync(host, 0);
        await host.Main.ToggleTagSlotCommand.ExecuteAsync(1);
        await host.Get<ITagService>().DeleteAsync(tag.Id, Ct);

        await host.Main.UndoCommand.ExecuteAsync(null);

        Assert.Equal("The records or tag of that step no longer exist; it was removed from the history.", host.Main.Status.Message);
        Assert.False(host.Main.UndoCommand.CanExecute(null));
    });

    [Fact]
    public Task The_export_wizard_splits_within_a_tag_group() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [.. Enumerable.Range(1, 20).Select(i => new InstructionPayload($"Soru {i}", "", $"Yanıt {i}"))]);
        var records = host.Main.Records;
        var ids = records.Items.Select(r => r.Id).ToList();
        var tags = host.Get<ITagService>();
        var commands = host.Get<IRecordCommandService>();
        var hukuk = await tags.CreateAsync("kategori:hukuk", Ct);
        var teknik = await tags.CreateAsync("kategori:teknik", Ct);
        await tags.CreateAsync("kullanılmayan:etiket", Ct);
        await commands.SetTagAsync(ids[..10], hukuk.Id, present: true, Ct);
        await commands.SetTagAsync(ids[10..], teknik.Id, present: true, Ct);
        await commands.SetStatusAsync(ids, RecordStatus.Approved, Ct);
        host.Files.ExportFolder = Path.Combine(host.Directory, "split");

        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            // Groups only from tags the dataset uses.
            Assert.Equal(["No strata", "Each tag (a record's first tag)", "Group “kategori”"], wizard.StratifyOptions.Select(o => o.Label));
            Assert.True(wizard.CanStratify);
            wizard.SelectedSplitMode = wizard.SplitModes.Single(m => m.Mode == SplitMode.Standard);
            wizard.SelectedStratify = wizard.StratifyOptions[2];
            await wizard.PendingWork;
            Assert.Equal("kategori", wizard.Options!.Split!.StratifyBy);
            Assert.StartsWith("Within every tag", wizard.SplitHint, StringComparison.Ordinal);
            await wizard.ExportCommand.ExecuteAsync(null);
        };
        await host.Main.ExportCommand.ExecuteAsync(null);

        // Exact shares in each category: 8 / 1 / 1 of 10.
        var folder = host.Files.ExportFolder;
        Assert.Equal(16, (await File.ReadAllLinesAsync(Path.Combine(folder, "train.jsonl"), Ct)).Length);
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(folder, "val.jsonl"), Ct)).Length);
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(folder, "test.jsonl"), Ct)).Length);

        // The next export starts from the same choice.
        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.Equal(SplitMode.Standard, wizard.SelectedSplitMode!.Mode);
            Assert.Equal("Group “kategori”", wizard.SelectedStratify!.Label);
        };
        await host.Main.ExportCommand.ExecuteAsync(null);
    });

    [Fact]
    public Task A_dataset_without_tags_offers_no_strata() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(2);
        await host.Get<ITagService>().CreateAsync("başka:veri", Ct); // used by no record

        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            Assert.Equal(["No strata"], wizard.StratifyOptions.Select(o => o.Label));
            Assert.False(wizard.CanStratify);
            Assert.False(wizard.IsStratified);
        };
        await host.Main.ExportCommand.ExecuteAsync(null);
    });

    [Fact]
    public Task The_versions_window_shows_tag_changes() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetWithRecordsAsync(2);
        var ids = host.Main.Records.Items.Select(r => r.Id).ToList();
        var tags = host.Get<ITagService>();
        var commands = host.Get<IRecordCommandService>();
        var eski = await tags.CreateAsync("eski", Ct);
        var yeni = await tags.CreateAsync("yeni", Ct);
        await commands.SetTagAsync([ids[0]], eski.Id, present: true, Ct);
        await host.Get<IVersionService>().CreateAsync(dataset.Id, "1.0.0", null, cancellationToken: Ct);
        await commands.SetTagAsync([ids[0]], eski.Id, present: false, Ct);
        await commands.SetTagAsync([ids[0]], yeni.Id, present: true, Ct);
        await host.Main.Tags.ReloadAsync();

        host.Dialogs.VersionsScript = async window =>
        {
            await window.InitializeAsync();
            Assert.Equal("1 tag changes · 1 unchanged", window.SummaryText);
            var change = Assert.Single(window.Changes);
            Assert.Equal(DifferenceKind.TagsChanged, change.Kind);
            Assert.Equal("Tags", change.KindText);

            window.SelectedChange = change;
            await DesktopTestHost.WaitUntilAsync(() => window.Fields.Any(f => f.Label == "Tags"));
            var field = window.Fields.Single(f => f.Label == "Tags");
            Assert.Equal("Changed", field.Badge);
            Assert.Equal([new DiffSegment(DiffKind.Deleted, "eski")], field.OldSegments);
            Assert.Equal([new DiffSegment(DiffKind.Inserted, "yeni")], field.NewSegments);
        };
        await host.Main.VersionsCommand.ExecuteAsync(null);
    });

    private static async Task SelectAsync(DesktopTestHost host, int index)
    {
        var row = host.Main.Records.Items[index];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
    }
}
