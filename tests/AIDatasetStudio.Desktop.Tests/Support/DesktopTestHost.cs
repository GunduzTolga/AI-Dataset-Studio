using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Infrastructure;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AIDatasetStudio.Desktop.Tests.Support;

/// <summary>
/// The real service graph (infrastructure + view models) over temporary files, with scripted dialogs.
/// Use from inside a headless UI session; view models expect the UI dispatcher.
/// </summary>
internal sealed class DesktopTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public DesktopTestHost()
    {
        Directory = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        _services = new ServiceCollection()
            .AddInfrastructure(Path.Combine(Directory, "settings"))
            .AddDesktop(() => null)
            .AddSingleton<IDialogService>(Dialogs)
            .AddSingleton<IFileDialogService>(Files)
            .BuildServiceProvider();
    }

    public string Directory { get; }

    public FakeDialogs Dialogs { get; } = new();

    public FakeFileDialogs Files { get; } = new();

    public MainWindowViewModel Main => Get<MainWindowViewModel>();

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public string PathFor(string name) => Path.Combine(Directory, name + WorkspaceFile.Extension);

    /// <summary>
    /// Creates a workspace through the File › New command and waits until the tree is ready.
    /// </summary>
    public async Task<string> CreateWorkspaceAsync(string name = "Test")
    {
        var path = PathFor(name);
        Files.CreatePath = path;
        await Main.NewWorkspaceCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => Main.Explorer.IsWorkspaceOpen);
        return path;
    }

    /// <summary>
    /// Runs posted UI work until <paramref name="condition"/> holds.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    public IUserSettings Settings => Get<IUserSettings>();

    public async ValueTask DisposeAsync()
    {
        await Get<IWorkspaceService>().CloseAsync();
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public Queue<string?> TextAnswers { get; } = new();

    public NewDatasetRequest? DatasetAnswer { get; set; }

    public object? ChoiceAnswer { get; set; }

    public bool ConfirmAnswer { get; set; } = true;

    public List<string> ConfirmMessages { get; } = [];

    public List<bool> ConfirmWasDestructive { get; } = [];

    public Task<string?> PromptTextAsync(string title, string message, string initialText) =>
        Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);

    public Task<NewDatasetRequest?> PromptNewDatasetAsync() => Task.FromResult(DatasetAnswer);

    public Task<T?> ChooseAsync<T>(string title, string message, IReadOnlyList<(T Value, string Label)> options, int selectedIndex)
        where T : struct => Task.FromResult(ChoiceAnswer is T value ? value : (T?)null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        ConfirmMessages.Add(message);
        ConfirmWasDestructive.Add(isDestructive);
        return Task.FromResult(ConfirmAnswer);
    }

    /// <summary>
    /// What the user does in the import wizard; by default reads the file and imports it as detected.
    /// </summary>
    public Func<ImportWizardViewModel, Task> ImportWizardScript { get; set; } = async wizard =>
    {
        await wizard.InitializeAsync();
        await wizard.ImportCommand.ExecuteAsync(null);
    };

    public ImportWizardViewModel? LastImportWizard { get; private set; }

    public async Task<ImportResult?> ShowImportWizardAsync(ImportWizardViewModel wizard)
    {
        LastImportWizard = wizard;
        await ImportWizardScript(wizard);
        return wizard.Result;
    }
}

internal sealed class FakeFileDialogs : IFileDialogService
{
    public string? CreatePath { get; set; }

    public string? OpenPath { get; set; }

    public Task<string?> PickWorkspaceToOpenAsync() => Task.FromResult(OpenPath);

    public Task<string?> PickWorkspaceToCreateAsync(string suggestedName) => Task.FromResult(CreatePath);

    public string? ImportPath { get; set; }

    public IReadOnlyList<string> ImportExtensions { get; private set; } = [];

    public Task<string?> PickImportFileAsync(IReadOnlyList<string> extensions)
    {
        ImportExtensions = extensions;
        return Task.FromResult(ImportPath);
    }
}
