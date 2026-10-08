using System.Globalization;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Localization;
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
    private readonly Localizer _localizer;

    // The status is kept as a resource key plus arguments so it can be re-rendered
    // when the UI language changes.
    private string _statusKey = "Status_Ready";
    private object?[] _statusArgs = [];

    public MainWindowViewModel(IWorkspaceService workspaces, IFileDialogService dialogs, Localizer localizer)
    {
        _workspaces = workspaces;
        _dialogs = dialogs;
        _localizer = localizer;
        _workspaces.CurrentChanged += (_, _) => Dispatcher.UIThread.Post(OnWorkspaceChanged);
        _localizer.CultureChanged += (_, _) => OnLanguageChanged();
    }

    public string StatusMessage => _localizer.Format(_statusKey, _statusArgs);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewWorkspaceCommand), nameof(OpenWorkspaceCommand), nameof(CloseWorkspaceCommand))]
    public partial bool IsBusy { get; set; }

    public WorkspaceInfo? Workspace => _workspaces.Current;

    public bool HasWorkspace => Workspace is not null;

    public string Title => Workspace is { } workspace ? $"{workspace.Name} — {AppTitle}" : AppTitle;

    public bool IsEnglish => _localizer.Culture.Equals(Localizer.English);

    public bool IsTurkish => _localizer.Culture.Equals(Localizer.Turkish);

    [RelayCommand(CanExecute = nameof(CanRunWorkspaceCommand))]
    private async Task NewWorkspaceAsync()
    {
        var path = await _dialogs.PickWorkspaceToCreateAsync(_localizer["Workspace_DefaultName"]);
        if (path is null)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        await RunAsync(() => _workspaces.CreateAsync(path, name, overwrite: true), "Status_WorkspaceCreated", name);
    }

    [RelayCommand(CanExecute = nameof(CanRunWorkspaceCommand))]
    private async Task OpenWorkspaceAsync()
    {
        var path = await _dialogs.PickWorkspaceToOpenAsync();
        if (path is null)
        {
            return;
        }

        await RunAsync(() => _workspaces.OpenAsync(path), "Status_WorkspaceOpened", Path.GetFileName(path));
    }

    [RelayCommand(CanExecute = nameof(CanCloseWorkspace))]
    private Task CloseWorkspaceAsync() => RunAsync(() => _workspaces.CloseAsync(), "Status_WorkspaceClosed");

    [RelayCommand]
    private void SetLanguage(string cultureName) => _localizer.SetCulture(CultureInfo.GetCultureInfo(cultureName));

    private bool CanRunWorkspaceCommand() => !IsBusy;

    private bool CanCloseWorkspace() => !IsBusy && HasWorkspace;

    private async Task RunAsync(Func<Task> operation, string successKey, params object?[] successArgs)
    {
        IsBusy = true;
        try
        {
            await operation();
            SetStatus(successKey, successArgs);
        }
        catch (WorkspaceFileException ex)
        {
            SetStatus(ErrorKey(ex.Error), Path.GetFileName(ex.FilePath), ex.InnerException?.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Error_FileAccess", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static string ErrorKey(WorkspaceFileError error) => error switch
    {
        WorkspaceFileError.NotFound => "Error_WorkspaceNotFound",
        WorkspaceFileError.AlreadyExists => "Error_WorkspaceAlreadyExists",
        WorkspaceFileError.NotAWorkspace => "Error_NotAWorkspace",
        WorkspaceFileError.CreatedByNewerVersion => "Error_CreatedByNewerVersion",
        WorkspaceFileError.Corrupted => "Error_WorkspaceCorrupted",
        WorkspaceFileError.Unavailable => "Error_WorkspaceUnavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void OnWorkspaceChanged()
    {
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(Title));
        CloseWorkspaceCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsTurkish));
    }
}
