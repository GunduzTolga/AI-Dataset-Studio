using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace AIDatasetStudio.Desktop.Tests.Views;

[Collection(LanguageCollection.Name)]
public sealed class MainWindowLanguageTests
{
    [Fact]
    public Task Switching_the_language_updates_visible_text_without_restart() => UiTest.RunAsync(async () =>
    {
        var localizer = Localizer.Instance;
        var original = localizer.Culture;
        await using var host = new DesktopTestHost();
        try
        {
            localizer.SetCulture(Localizer.English);
            var window = new MainWindow { DataContext = host.Main };
            window.Show();

            Assert.Contains("EXPLORER", VisibleTexts(window));
            Assert.Equal("Ready", host.Main.Status.Message);

            host.Main.SetLanguageCommand.Execute("tr");
            Dispatcher.UIThread.RunJobs();

            var texts = VisibleTexts(window);
            Assert.Contains("GEZGİN", texts);
            Assert.DoesNotContain("EXPLORER", texts);
            Assert.Equal("Hazır", host.Main.Status.Message);
            Assert.True(host.Main.IsTurkish);
            Assert.Equal("tr", host.Settings.UiLanguage);
            window.Close();
        }
        finally
        {
            localizer.SetCulture(original);
        }
    });

    private static List<string> VisibleTexts(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToList();
}
