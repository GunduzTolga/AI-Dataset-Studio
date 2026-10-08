using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace AIDatasetStudio.Desktop.Tests.Views;

public sealed class MainWindowLanguageTests
{
    // Runs on Avalonia's headless UI thread (framework-agnostic session; the xUnit adapter
    // package targets an older xUnit major version).
    [Fact]
    public Task Switching_the_language_updates_visible_text_without_restart() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MainWindowLanguageTests).Assembly)
            .Dispatch(SwitchLanguage, TestContext.Current.CancellationToken);

    private static void SwitchLanguage()
    {
        var localizer = Localizer.Instance;
        var original = localizer.Culture;
        try
        {
            localizer.SetCulture(Localizer.English);
            var viewModel = new MainWindowViewModel(new NoWorkspace(), new NoDialogs(), localizer);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            Assert.Contains("EXPLORER", VisibleTexts(window));
            Assert.Equal("Ready", viewModel.StatusMessage);

            viewModel.SetLanguageCommand.Execute("tr");
            Dispatcher.UIThread.RunJobs();

            var texts = VisibleTexts(window);
            Assert.Contains("GEZGİN", texts);
            Assert.DoesNotContain("EXPLORER", texts);
            Assert.Equal("Hazır", viewModel.StatusMessage);
            Assert.True(viewModel.IsTurkish);
            window.Close();
        }
        finally
        {
            localizer.SetCulture(original);
        }
    }

    private static List<string> VisibleTexts(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty).ToList();

    private sealed class NoWorkspace : IWorkspaceService
    {
        public WorkspaceInfo? Current => null;

        public event EventHandler? CurrentChanged { add { } remove { } }

        public Task<WorkspaceInfo> CreateAsync(string filePath, string name, bool overwrite = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceInfo> OpenAsync(string filePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoDialogs : IFileDialogService
    {
        public Task<string?> PickWorkspaceToOpenAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickWorkspaceToCreateAsync(string suggestedName) => Task.FromResult<string?>(null);
    }
}
