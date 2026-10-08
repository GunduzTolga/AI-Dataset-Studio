using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const string AppTitle = "AI Dataset Studio";

    private readonly IWorkspaceService _workspaces;
    private readonly IFileDialogService _dialogs;

    public MainWindowViewModel(IWorkspaceService workspaces, IFileDialogService dialogs)
    {
        _workspaces = workspaces;
        _dialogs = dialogs;
        _workspaces.CurrentChanged += (_, _) => Dispatcher.UIThread.Post(OnWorkspaceChanged);
    }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "Ready";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewWorkspaceCommand), nameof(OpenWorkspaceCommand), nameof(CloseWorkspaceCommand))]
    public partial bool IsBusy { get; set; }

    public WorkspaceInfo? Workspace => _workspaces.Current;

    public bool HasWorkspace => Workspace is not null;

    public string Title => Workspace is { } workspace ? $"{workspace.Name} — {AppTitle}" : AppTitle;

    [RelayCommand(CanExecute = nameof(CanRunWorkspaceCommand))]
    private async Task NewWorkspaceAsync()
    {
        var path = await _dialogs.PickWorkspaceToCreateAsync("Untitled");
        if (path is null)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        await RunAsync(() => _workspaces.CreateAsync(path, name, overwrite: true), $"Created workspace '{name}'.");
    }

    [RelayCommand(CanExecute = nameof(CanRunWorkspaceCommand))]
    private async Task OpenWorkspaceAsync()
    {
        var path = await _dialogs.PickWorkspaceToOpenAsync();
        if (path is null)
        {
            return;
        }

        await RunAsync(() => _workspaces.OpenAsync(path), $"Opened '{Path.GetFileName(path)}'.");
    }

    [RelayCommand(CanExecute = nameof(CanCloseWorkspace))]
    private Task CloseWorkspaceAsync() => RunAsync(() => _workspaces.CloseAsync(), "Workspace closed.");

    private bool CanRunWorkspaceCommand() => !IsBusy;

    private bool CanCloseWorkspace() => !IsBusy && HasWorkspace;

    private async Task RunAsync(Func<Task> operation, string successMessage)
    {
        IsBusy = true;
        try
        {
            await operation();
            StatusMessage = successMessage;
        }
        catch (Exception ex) when (ex is WorkspaceFileException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnWorkspaceChanged()
    {
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(Title));
        CloseWorkspaceCommand.NotifyCanExecuteChanged();
    }
}
