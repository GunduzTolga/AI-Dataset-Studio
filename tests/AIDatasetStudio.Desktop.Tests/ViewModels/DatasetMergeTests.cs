using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Tools › Merge Datasets (M21): datasets of one type from any projects into a new dataset.
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
}
