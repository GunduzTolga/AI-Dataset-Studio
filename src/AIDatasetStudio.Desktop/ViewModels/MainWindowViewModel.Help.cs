using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// Help menu: updates, the about box and unexpected errors.
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Look for a new version at startup (off by default; no network request otherwise).
    /// </summary>
    public bool CheckForUpdatesAtStartup
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (value != _settings.CheckForUpdates)
            {
                _settings.SetCheckForUpdates(value);
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// A newer version found by a check; installed from Help › Check for Updates.
    /// </summary>
    [ObservableProperty]
    public partial AvailableUpdate? AvailableUpdate { get; private set; }

    /// <summary>
    /// Checks quietly at startup when the user turned that on; only reports a newer version.
    /// </summary>
    public async Task CheckForUpdatesAtStartupAsync()
    {
        if (!_settings.CheckForUpdates || !_updates.IsSupported)
        {
            return;
        }

        try
        {
            if (await _updates.CheckAsync() is { } update)
            {
                AvailableUpdate = update;
                Status.Set("Status_UpdateAvailable", update.Version);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            // Offline or the release feed is unreachable: stay quiet at startup.
        }
    }

    /// <summary>
    /// Logs an error nothing else handled and says where the log is.
    /// </summary>
    public void ReportUnexpectedError(Exception exception)
    {
        var log = CrashLog.Write(exception);
        Status.Set(log is null ? "Error_UnexpectedNoLog" : "Error_Unexpected", log);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task CheckForUpdatesAsync()
    {
        if (!_updates.IsSupported)
        {
            Status.Set("Status_UpdatesNotInstalled", AppInfo.Version);
            return;
        }

        IsBusy = true;
        try
        {
            Status.Set("Status_CheckingForUpdates");
            var update = AvailableUpdate ?? await _updates.CheckAsync();
            AvailableUpdate = update;
            if (update is null)
            {
                Status.Set("Status_UpToDate", AppInfo.Version);
                return;
            }

            if (!await _dialogs.ConfirmAsync(
                    _localizer["Dialog_UpdateTitle"],
                    _localizer.Format("Dialog_UpdateMessage", update.Version, AppInfo.Version),
                    _localizer["Dialog_UpdateConfirm"]))
            {
                Status.Set("Status_UpdateAvailable", update.Version);
                return;
            }

            var progress = new Progress<int>(percent => Status.Set("Status_DownloadingUpdate", update.Version, percent));
            await _updates.DownloadAsync(update, progress);

            // Save pending edits and close the workspace cleanly: the app exits to install.
            if (!await Details.SaveIfDirtyAsync())
            {
                return;
            }

            await Overview.StopAsync();
            await _workspaces.CloseAsync();
            _updates.RestartToApply(update);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            Status.Set("Status_UpdateFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task AboutAsync() => _dialogs.ShowAboutAsync(new AboutViewModel(_shell, _localizer));
}
