using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Catalog;
using AIDatasetStudio.Desktop.Views.Dialogs;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The Merge Datasets window, rendered (M21).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class DatasetMergeWindowTests
{
    public DatasetMergeWindowTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task The_merge_runs_off_the_window_and_closing_stops_it_first() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var first = await host.CreateDatasetAsync(DatasetType.Instruction, [.. Enumerable.Range(0, 30).Select(i => new InstructionPayload($"Soru {i}", "", "Cevap"))]);
        var catalog = host.Get<ICatalogService>();
        var second = await catalog.CreateDatasetAsync((await catalog.GetProjectsAsync()).Single().Id, "Second", DatasetType.Instruction);
        await host.Get<IRecordCommandService>().AddAsync(second, new InstructionPayload("Başka soru", "", "Cevap"), RecordLineage.Manual());
        var merge = new DatasetMergeViewModel(host.Get<IDatasetMergeService>(), host.Get<Localizer>(), await catalog.GetProjectsAsync(), first.Id);
        merge.Sources.Single(s => s.Name == "Second").IsChecked = true;
        var window = new DatasetMergeWindow { DataContext = merge };
        window.Show();

        // The merge does not hold up the window: it shows progress, locks the choices and can be stopped.
        var running = merge.MergeCommand.ExecuteAsync(null);
        Assert.True(merge.IsMerging);
        Assert.False(Find<ScrollViewer>(window, "SourceList").IsEffectivelyEnabled);
        Assert.False(Find<TextBox>(window, "NameBox").IsEffectivelyEnabled);

        // Closing from the title bar stops the merge first, then closes.
        window.Close();
        Assert.True(window.IsVisible);
        await running;
        await DesktopTestHost.WaitUntilAsync(() => !window.IsVisible);

        // Stopped in time, nothing is left; finished first, the new dataset is whole.
        var made = (await catalog.GetProjectsAsync()).Single().Datasets.Where(d => d.Name == merge.Name).ToList();
        if (merge.Result is null)
        {
            Assert.Empty(made);
        }
        else
        {
            Assert.Equal(31, Assert.Single(made).RecordCount);
        }
    });

    private static T Find<T>(Window window, string name)
        where T : Control
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
    }
}
