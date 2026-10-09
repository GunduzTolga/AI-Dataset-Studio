using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Desktop.Controls;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Desktop.ViewModels.Versions;
using AIDatasetStudio.Desktop.Views.Dialogs;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The versions window with its view: the lists select items by themselves while they fill.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class VersionsWindowTests
{
    public VersionsWindowTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Opening_the_window_compares_the_working_copy_with_the_latest_version() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("İade süresi?", "", "14 gün."),
            new InstructionPayload("Mesai saatleri?", "", "09.00 – 18.00."),
        ]);
        var versions = host.Get<IVersionService>();
        await versions.CreateAsync(dataset.Id, "1.0.0", null);
        await versions.CreateAsync(dataset.Id, "1.1.0", null);
        await host.Get<Application.Records.IRecordCommandService>().UpdatePayloadAsync(
            host.Main.Records.Items[0].Id, new InstructionPayload("İade süresi?", "", "30 gün."));
        var viewModel = new VersionsViewModel(
            versions, host.Get<RecordOperations>(), host.Dialogs, Localizer.Instance, host.Get<TagCatalog>(), new VersionsTarget(dataset.Id, dataset.Name, dataset.Type, 2));
        var window = new VersionsWindow { DataContext = viewModel };

        window.Show();
        await DesktopTestHost.WaitUntilAsync(() => viewModel.Fields.Count == 3);

        Assert.Equal(["Working copy", "v1.1.0", "v1.0.0"], viewModel.Items.Select(i => i.Title));
        Assert.Equal(viewModel.Items[1], viewModel.CompareWith);
        Assert.Equal("v1.1.0 → Working copy", viewModel.ComparisonTitle);
        Assert.Equal(viewModel.KindFilters[0], viewModel.SelectedKindFilter);
        Assert.Single(viewModel.Changes);

        // The edit is drawn with the theme's colours behind the changed words.
        var blocks = window.GetLogicalDescendants().OfType<DiffTextBlock>().Where(b => b.IsEffectivelyVisible).ToList();
        var inserted = blocks.SelectMany(b => b.Inlines!.OfType<Run>()).Single(r => r.Text == "30");
        Assert.True(window.TryFindResource("DiffInsertedBrush", window.ActualThemeVariant, out var brush));
        Assert.Equal(((ISolidColorBrush)brush!).Color, ((ISolidColorBrush)inserted.Background!).Color);
        var deleted = blocks.SelectMany(b => b.Inlines!.OfType<Run>()).Single(r => r.Text == "14");
        Assert.NotNull(deleted.Background);

        // Choosing a version shows it against the one before.
        viewModel.SelectedItem = viewModel.Items[1];
        await viewModel.PendingWork;
        Assert.Equal("v1.0.0 → v1.1.0", viewModel.ComparisonTitle);
        Assert.True(viewModel.IsIdentical);
        window.Close();
    });
}
