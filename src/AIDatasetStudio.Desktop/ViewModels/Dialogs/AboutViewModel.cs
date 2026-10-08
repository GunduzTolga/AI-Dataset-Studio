using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Dialogs;

/// <summary>
/// Help › About: version, where the app keeps its own files, and the open-source components.
/// </summary>
public sealed partial class AboutViewModel(IShellService shell, Localizer localizer) : ViewModelBase
{
    /// <summary>
    /// Raised when the dialog should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    public string VersionText => localizer.Format("About_Version", AppInfo.Version);

    /// <summary>
    /// Settings and error logs; workspaces live wherever the user saves them.
    /// </summary>
    public string DataFolder => AppInfo.DataDirectory;

    public string LogFolder => AppInfo.LogDirectory;

    [RelayCommand]
    private Task OpenLogFolderAsync() => shell.OpenFolderAsync(LogFolder);

    [RelayCommand]
    private Task OpenNoticesAsync() => shell.OpenFileAsync(AppInfo.NoticesPath);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
