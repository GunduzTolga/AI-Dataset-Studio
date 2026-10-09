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
/// The Merge Datasets window, rendered (M21, M23).
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
        var merge = new DatasetMergeViewModel(host.Get<IDatasetMergeService>(), host.Files, host.Get<Localizer>(), await catalog.GetProjectsAsync(), first.Id);
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

    [Fact]
    public Task Datasets_of_another_file_are_listed_under_its_name() => UiTest.RunAsync(async () =>
    {
        await using var other = new DesktopTestHost();
        await other.CreateDatasetAsync(DatasetType.Chat, []);
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, []);
        await using var merge = new DatasetMergeViewModel(host.Get<IDatasetMergeService>(), host.Files, host.Get<Localizer>(), await host.Get<ICatalogService>().GetProjectsAsync());
        var window = new DatasetMergeWindow { DataContext = merge };
        window.Show();
        Assert.Equal("From this workspace", Find<TextBlock>(window, "SourceText").Text);
        Assert.False(Find<Button>(window, "ThisWorkspaceButton").IsVisible);

        host.Files.OpenPath = other.PathFor("Test");
        Find<Button>(window, "ChooseFileButton").Command!.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => merge.IsFromOtherFile);

        Assert.Equal("From Test.adstudio", Find<TextBlock>(window, "SourceText").Text);
        Assert.True(Find<Button>(window, "ThisWorkspaceButton").IsVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible && c.DataContext is MergeSourceItem { Type: DatasetType.Chat });
        window.Close();

        // Closed while a file is read: the file read is let go, not listed.
        await using var closed = new DatasetMergeViewModel(host.Get<IDatasetMergeService>(), host.Files, host.Get<Localizer>(), await host.Get<ICatalogService>().GetProjectsAsync());
        var opening = closed.ChooseFileCommand.ExecuteAsync(null);
        Assert.True(closed.IsOpeningFile);
        await closed.DisposeAsync();
        await opening;
        Assert.False(closed.IsFromOtherFile);
        Assert.Equal(["Project"], closed.Groups.Select(g => g.Name));
    });

    private static T Find<T>(Window window, string name)
        where T : Control
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
    }
}
