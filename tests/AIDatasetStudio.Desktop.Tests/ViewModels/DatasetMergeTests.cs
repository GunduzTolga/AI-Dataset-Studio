using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Tools › Merge Datasets (M21): datasets of one type from any projects, or from another
/// workspace file (M23), into a new dataset.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class DatasetMergeTests
{
    public DatasetMergeTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Datasets_of_one_type_are_merged_into_a_new_dataset_that_is_then_shown() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var orders = await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("Kargo ne zaman gelir?", "", "1-3 iş günü."),
            new InstructionPayload("Fatura?", "", "Hesabım sayfasında."),
        ]);
        var catalog = host.Get<ICatalogService>();
        var support = await catalog.CreateProjectAsync("Destek");
        var returns = await catalog.CreateDatasetAsync(support, "İade SSS", DatasetType.Instruction);
        var chats = await catalog.CreateDatasetAsync(support, "Sohbetler", DatasetType.Chat);
        await host.Get<IRecordCommandService>().AddRangeAsync(returns,
        [
            new NewRecord(new InstructionPayload("İade süresi?", "", "14 gün."), RecordLineage.Manual()),
            new NewRecord(new InstructionPayload("Kargo ne zaman gelir?", "", "1-3 iş günü."), RecordLineage.Manual()),
        ]);
        await host.Main.Explorer.ReloadAsync();
        host.Main.Explorer.SelectedNode = host.Main.Explorer.FindDataset(orders.Id);

        host.Dialogs.MergeScript = async merge =>
        {
            // The shown dataset is chosen; only datasets of its type can join it.
            Assert.Equal(["Project", "Destek"], merge.Groups.Select(g => g.Name));
            Assert.Equal(["Dataset"], merge.Chosen.Select(c => c.Name));
            Assert.False(merge.Sources.Single(s => s.Id == chats).IsEnabled);
            Assert.Equal("Choose another Instruction dataset to merge with.", merge.SummaryText);
            Assert.False(merge.MergeCommand.CanExecute(null));

            merge.Sources.Single(s => s.Id == returns).IsChecked = true;
            Assert.Equal("Dataset + İade SSS", merge.Name);
            Assert.Equal("2 datasets · 4 records · Instruction", merge.SummaryText);
            Assert.Equal("Project", merge.SelectedTarget!.Name);
            merge.Name = "Tüm SSS";
            merge.SelectedTarget = merge.Targets.Single(t => t.ProjectId == support);
            await merge.MergeCommand.ExecuteAsync(null);

            Assert.True(merge.IsDone);
            Assert.Equal("'Tüm SSS' was created with 3 records. Exact copies left out: 1.", merge.ResultText);
        };

        await host.Main.Explorer.MergeDatasetsCommand.ExecuteAsync(null);

        var shown = host.Main.Explorer.SelectedDataset!;
        Assert.Equal(("Tüm SSS", "Destek", 3L), (shown.Name, shown.ProjectName, shown.RecordCount));
        Assert.Equal("Merged into 'Tüm SSS': 3 records", host.Main.Status.Message);
        Assert.Equal(2, host.Main.Explorer.FindDataset(orders.Id)!.RecordCount);
    });

    [Fact]
    public Task Datasets_of_another_workspace_file_are_brought_over_and_the_file_stays_as_it_was() => UiTest.RunAsync(async () =>
    {
        // Another workspace file, open in another window, with a tagged record.
        await using var other = new DesktopTestHost();
        var theirs = await other.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("İade süresi?", "", "14 gün."),
            new InstructionPayload("Kargo?", "", "1-3 iş günü."),
        ]);
        var tags = other.Get<ITagService>();
        var tag = await tags.CreateAsync("iade");
        await tags.UpdateAsync(tag.Id, tag.Name, "#E11D48", tag.Shortcut);
        var theirRecords = await other.Get<IRecordQueryService>().GetPageAsync(theirs.Id, RecordFilter.None, after: null, pageSize: 10);
        await other.Get<IRecordCommandService>().SetTagAsync([theirRecords.Items[0].Id], tag.Id, present: true);
        var otherPath = other.PathFor("Test");

        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Soru", "", "Cevap")]);
        var notes = Path.Combine(host.Directory, "notes" + Application.Workspaces.WorkspaceFile.Extension);
        await File.WriteAllTextAsync(notes, new string('x', 4096));

        host.Dialogs.MergeScript = async merge =>
        {
            Assert.Equal("From this workspace", merge.SourceText);
            Assert.Equal(["Dataset"], merge.Chosen.Select(c => c.Name));
            Assert.False(merge.UseThisWorkspaceCommand.CanExecute(null));

            // No file chosen: nothing changes.
            await merge.ChooseFileCommand.ExecuteAsync(null);
            Assert.False(merge.IsFromOtherFile);

            // The open workspace itself, and a file that is not a workspace, are refused.
            host.Files.OpenPath = host.PathFor("Test");
            await merge.ChooseFileCommand.ExecuteAsync(null);
            Assert.Equal(("The file you chose is the open workspace.", false), (merge.ErrorMessage, merge.IsFromOtherFile));
            host.Files.OpenPath = notes;
            await merge.ChooseFileCommand.ExecuteAsync(null);
            Assert.Equal("'notes.adstudio' is not an AI Dataset Studio workspace.", merge.ErrorMessage);

            // The other file's datasets are listed instead; the choice starts over.
            host.Files.OpenPath = otherPath;
            await merge.ChooseFileCommand.ExecuteAsync(null);
            Assert.Null(merge.ErrorMessage);
            Assert.Equal(("From Test.adstudio", true), (merge.SourceText, merge.IsFromOtherFile));
            Assert.Equal(["Project"], merge.Groups.Select(g => g.Name));
            Assert.Empty(merge.Chosen);
            Assert.Equal("Choose the datasets to bring over.", merge.SummaryText);

            // Back to this workspace and to the file again.
            await merge.UseThisWorkspaceCommand.ExecuteAsync(null);
            Assert.Equal(("From this workspace", 1), (merge.SourceText, merge.Sources.Count()));
            await merge.ChooseFileCommand.ExecuteAsync(null);

            // One dataset of another file is enough.
            merge.Sources.Single().IsChecked = true;
            Assert.Equal("1 dataset · 2 records · Instruction", merge.SummaryText);
            merge.Name = "Mağaza SSS";
            await merge.MergeCommand.ExecuteAsync(null);
            Assert.Equal("'Mağaza SSS' was created with 2 records.", merge.ResultText);
        };

        await host.Main.Explorer.MergeDatasetsCommand.ExecuteAsync(null);

        var shown = host.Main.Explorer.SelectedDataset!;
        Assert.Equal(("Mağaza SSS", "Project", 2L), (shown.Name, shown.ProjectName, shown.RecordCount));
        var copies = await host.Get<IRecordQueryService>().GetPageAsync(shown.Id, RecordFilter.None, after: null, pageSize: 10);
        var added = Assert.Single(await host.Get<ITagService>().ListAsync());
        Assert.Equal(("iade", "#E11D48", (int?)null), (added.Name, added.ColorHex, added.Shortcut));
        Assert.Equal([added.Id], (await host.Get<IRecordQueryService>().GetAsync(copies.Items[0].Id))!.TagIds);

        // The other file is as it was.
        Assert.Equal(2, other.Main.Explorer.FindDataset(theirs.Id)!.RecordCount);
        Assert.Equal(2, (await other.Get<IRecordQueryService>().GetPageAsync(theirs.Id, RecordFilter.None, after: null, pageSize: 10)).Items.Count);
    });
}
