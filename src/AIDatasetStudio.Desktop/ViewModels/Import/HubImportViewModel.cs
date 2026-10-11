using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Import;

/// <summary>
/// A file of a Hugging Face dataset, as listed to choose from.
/// </summary>
public sealed record HubFileItem(HubFile File, string SizeText)
{
    public string Path => File.Path;
}

/// <summary>
/// File › Import from Hugging Face (M25): a dataset's importable files are listed, one is
/// downloaded, and the import wizard then reads it like any file.
/// </summary>
public sealed partial class HubImportViewModel(IHuggingFaceHub hub, Localizer localizer, string target) : ViewModelBase
{
    private CancellationTokenSource? _work;

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// "Into “Destek SSS”".
    /// </summary>
    public string TargetText { get; } = localizer.Format("Hub_Target", target);

    /// <summary>
    /// What the user typed: "owner/name" or the dataset's page address.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ListCommand))]
    public partial string DatasetId { get; set; } = string.Empty;

    public ObservableCollection<HubFileItem> Files { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    public partial HubFileItem? SelectedFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ListCommand), nameof(DownloadCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// The dataset whose files are listed, as "owner/name".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    public partial string? ListedId { get; private set; }

    public bool HasFiles => Files.Count > 0;

    /// <summary>
    /// The file downloaded to import; <c>null</c> until then.
    /// </summary>
    public string? DownloadedPath { get; private set; }

    /// <summary>
    /// The current listing or download; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    private bool CanList() => !IsBusy && !string.IsNullOrWhiteSpace(DatasetId);

    [RelayCommand(CanExecute = nameof(CanList))]
    private Task ListAsync()
    {
        PendingWork = RunAsync(async token =>
        {
            Files.Clear();
            ListedId = null;
            SelectedFile = null;
            var id = IHuggingFaceHub.ParseDatasetId(DatasetId) ?? throw new HubException(HubErrorKind.InvalidId, DatasetId);
            ProgressText = localizer.Format("Hub_Listing", id);
            foreach (var file in await hub.ListFilesAsync(id, token))
            {
                Files.Add(new HubFileItem(file, file.Size is { } size ? SizeText(size) : string.Empty));
            }

            ListedId = id;
            OnPropertyChanged(nameof(HasFiles));
            ProgressText = Files.Count == 0 ? null : localizer.Format("Hub_Found", Files.Count, id);
            if (Files.Count == 0)
            {
                ErrorMessage = localizer["Hub_NoFiles"];
            }

            // The training part is what is usually wanted; Parquet before JSON of the same part.
            SelectedFile = Files.OrderByDescending(f => f.Path.Contains("train", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.Path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        });
        return PendingWork;
    }

    private bool CanDownload() => !IsBusy && SelectedFile is not null && ListedId is not null;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync()
    {
        if (SelectedFile is not { } item || ListedId is not { } id)
        {
            return Task.CompletedTask;
        }

        PendingWork = RunAsync(async token =>
        {
            var total = item.File.Size;
            ProgressText = localizer.Format("Hub_Downloading", item.Path, total is { } size ? SizeText(size) : "?", SizeText(0));
            var progress = new Progress<long>(received =>
                ProgressText = localizer.Format("Hub_Downloading", item.Path, total is { } size ? SizeText(size) : "?", SizeText(received)));
            DownloadedPath = await hub.DownloadAsync(id, item.File, progress, token);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        return PendingWork;
    }

    /// <summary>
    /// Stops a listing or download, or closes the window.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_work is { } work)
        {
            work.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        using var source = new CancellationTokenSource();
        _work = source;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await work(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            ProgressText = null;
        }
        catch (HubException ex)
        {
            ProgressText = null;
            ErrorMessage = ex.Kind switch
            {
                HubErrorKind.InvalidId => localizer["Hub_InvalidId"],
                HubErrorKind.NotFound => localizer["Hub_NotFound"],
                HubErrorKind.AccessDenied => localizer["Hub_AccessDenied"],
                _ => localizer.Format("Hub_Unavailable", ex.InnerException?.Message ?? ex.Message),
            };
        }
        finally
        {
            _work = null;
            IsBusy = false;
        }
    }

    private static string SizeText(long bytes) => bytes switch
    {
        0 => "0 KB",
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, bytes / 1024.0):N0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):N1} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):N2} GB"),
    };
}
