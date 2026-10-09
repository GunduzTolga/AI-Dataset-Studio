using AIDatasetStudio.Desktop.Controls;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The comparison of chosen and rejected answers in the real editor (M10).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class PreferenceCompareViewTests
{
    public PreferenceCompareViewTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task The_compare_button_shows_the_answers_with_their_differences_marked() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Preference,
            [new PreferencePayload("İade süresi nedir?", "14 gün içinde iade edebilirsiniz.", "14 gün içinde iade edemezsiniz.")]);
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        Dispatcher.UIThread.RunJobs();

        var button = Visible<Button>(window).Single(b => b.Name == "CompareButton");
        Assert.DoesNotContain(Visible<Border>(window), b => b.Name == "PreferenceCompareCard");
        button.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Visible<Border>(window), b => b.Name == "PreferenceCompareCard");
        var runs = Visible<DiffTextBlock>(window).SelectMany(block => block.Inlines!.OfType<Run>()).ToList();
        Assert.True(window.TryFindResource("DiffInsertedBrush", window.ActualThemeVariant, out var inserted));
        Assert.Equal(((ISolidColorBrush)inserted!).Color, ((ISolidColorBrush)runs.Single(r => r.Text == "edebilirsiniz").Background!).Color);
        Assert.NotNull(runs.Single(r => r.Text == "edemezsiniz").Background);
        Assert.Null(runs.First(r => r.Text!.StartsWith("14", StringComparison.Ordinal)).Background);
        window.Close();
    });

    private static List<T> Visible<T>(Window window)
        where T : Control
    {
        window.UpdateLayout();
        return [.. window.GetVisualDescendants().OfType<T>().Where(control => control.IsEffectivelyVisible)];
    }
}
