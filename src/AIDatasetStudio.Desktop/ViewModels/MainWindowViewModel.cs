using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Settings;
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

    private static readonly int[] SampleSizes = [100, 1_000, 10_000, 100_000];

    private readonly IWorkspaceService _workspaces;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IRecordCommandService _commands;
    private readonly IRecordQueryService _queries;
    private readonly IUserSettings _settings;
    private readonly Localizer _localizer;

    public MainWindowViewModel(
        IWorkspaceService workspaces,
        IFileDialogService files,
        IDialogService dialogs,
        IRecordCommandService commands,
        IRecordQueryService queries,
        IUserSettings settings,
        Localizer localizer,
        StatusBarViewModel status,
        ExplorerViewModel explorer,
        RecordListViewModel records,
        RecordDetailsViewModel details)
    {
        _workspaces = workspaces;
        _files = files;
        _dialogs = dialogs;
        _commands = commands;
        _queries = queries;
        _settings = settings;
        _localizer = localizer;
        Status = status;
        Explorer = explorer;
        Records = records;
        Details = details;

        _workspaces.CurrentChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = OnWorkspaceChangedAsync());
        _localizer.CultureChanged += (_, _) => OnLanguageChanged();
        Explorer.PropertyChanged += OnExplorerPropertyChanged;
        Records.PropertyChanged += OnRecordsPropertyChanged;
        Details.StatusChanged += (_, change) => Records.UpdateStatus(change.RecordId, change.Status);
        Details.Deleted += (_, recordId) => Records.Remove(recordId);
        Theme = ThemeService.Parse(settings.UiTheme);
        RefreshRecentWorkspaces();
    }

    public StatusBarViewModel Status { get; }

    public ExplorerViewModel Explorer { get; }

    public RecordListViewModel Records { get; }

    public RecordDetailsViewModel Details { get; }

    public ObservableCollection<RecentWorkspaceViewModel> RecentWorkspaces { get; } = [];

    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewWorkspaceCommand), nameof(OpenWorkspaceCommand), nameof(OpenRecentCommand), nameof(CloseWorkspaceCommand), nameof(GenerateSampleDataCommand))]
    public partial bool IsBusy { get; set; }

    public WorkspaceInfo? Workspace => _workspaces.Current;

    public bool HasWorkspace => Workspace is not null;

    public string Title => Workspace is { } workspace ? $"{workspace.Name} — {AppTitle}" : AppTitle;

    public bool IsEnglish => _localizer.Culture.Equals(Localizer.English);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme))]
    public partial AppTheme Theme { get; private set; }

    public bool IsSystemTheme => Theme == AppTheme.System;

    public bool IsLightTheme => Theme == AppTheme.Light;

    public bool IsDarkTheme => Theme == AppTheme.Dark;

    public bool IsTurkish => _localizer.Culture.Equals(Localizer.Turkish);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task NewWorkspaceAsync()
    {
        var path = await _files.PickWorkspaceToCreateAsync(_localizer["Workspace_DefaultName"]);
        if (path is null)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        await RunWorkspaceOperationAsync(path, () => _workspaces.CreateAsync(path, name, overwrite: true), "Status_WorkspaceCreated", name);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenWorkspaceAsync()
    {
        var path = await _files.PickWorkspaceToOpenAsync();
        if (path is not null)
        {
            await OpenAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task OpenRecentAsync(string? path) => path is null ? Task.CompletedTask : OpenAsync(path);

    [RelayCommand(CanExecute = nameof(CanCloseWorkspace))]
    private async Task CloseWorkspaceAsync()
    {
        IsBusy = true;
        try
        {
            await _workspaces.CloseAsync();
            Status.Set("Status_WorkspaceClosed");
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SetLanguage(string cultureName)
    {
        _localizer.SetCulture(CultureInfo.GetCultureInfo(cultureName));
        _settings.SetUiLanguage(_localizer.Culture.Name);
    }

    [RelayCommand]
    private void SetTheme(string value)
    {
        var theme = Enum.Parse<AppTheme>(value, ignoreCase: true);
        ThemeService.Apply(theme);
        _settings.SetUiTheme(ThemeService.ToSetting(theme));
        Theme = theme;
    }

    private bool CanGenerateSampleData() => !IsBusy && Explorer.SelectedDataset is not null;

    [RelayCommand(CanExecute = nameof(CanGenerateSampleData))]
    private async Task GenerateSampleDataAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset)
        {
            return;
        }

        var options = SampleSizes.Select(size => (size, size.ToString("N0", CultureInfo.CurrentCulture))).ToList();
        var count = await _dialogs.ChooseAsync(
            _localizer["Dialog_SampleDataTitle"],
            _localizer.Format("Dialog_SampleDataMessage", dataset.Name),
            options,
            selectedIndex: 1);
        if (count is not { } total)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Status.Set("Status_SampleDataProgress", 0, total);
            var progress = new Progress<int>(done => Status.Set("Status_SampleDataProgress", done, total));
            var records = SampleData.Create(dataset.Type, total);
            // Off the UI thread: tens of thousands of awaited inserts would otherwise each hop back to it.
            var added = await Task.Run(() => _commands.AddRangeAsync(dataset.Id, records, progress));

            Explorer.SetRecordCount(dataset.Id, await _queries.CountAsync(dataset.Id, RecordFilter.None));
            await Records.ReloadAsync();
            Status.Set("Status_SampleDataAdded", added, dataset.Name);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsIdle() => !IsBusy;

    private bool CanCloseWorkspace() => !IsBusy && HasWorkspace;

    private Task OpenAsync(string path) =>
        RunWorkspaceOperationAsync(path, () => _workspaces.OpenAsync(path), "Status_WorkspaceOpened", Path.GetFileName(path));

    private async Task RunWorkspaceOperationAsync(string path, Func<Task> operation, string successKey, params object?[] successArgs)
    {
        IsBusy = true;
        try
        {
            await operation();
            _settings.AddRecentWorkspace(path);
            Status.Set(successKey, successArgs);
        }
        catch (WorkspaceFileException ex) when (ex.Error is WorkspaceFileError.NotFound)
        {
            _settings.RemoveRecentWorkspace(path);
            Status.TryReport(ex);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
            RefreshRecentWorkspaces();
        }
    }

    private async Task OnWorkspaceChangedAsync()
    {
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(Title));
        CloseWorkspaceCommand.NotifyCanExecuteChanged();

        Explorer.IsWorkspaceOpen = HasWorkspace;
        if (HasWorkspace)
        {
            try
            {
                await Explorer.ReloadAsync();
            }
            catch (Exception ex) when (Status.TryReport(ex))
            {
            }
        }
        else
        {
            Explorer.Clear();
        }
    }

    private void OnExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerViewModel.SelectedDataset) && Explorer.SelectedDataset != Records.Dataset)
        {
            GenerateSampleDataCommand.NotifyCanExecuteChanged();
            _ = Details.ShowAsync(null);
            _ = Records.ShowDatasetAsync(Explorer.SelectedDataset);
        }
    }

    private void OnRecordsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordListViewModel.SelectedItem))
        {
            _ = Details.ShowAsync(Records.SelectedItem?.Id);
        }
    }

    private void RefreshRecentWorkspaces()
    {
        RecentWorkspaces.Clear();
        foreach (var path in _settings.RecentWorkspaces)
        {
            RecentWorkspaces.Add(new RecentWorkspaceViewModel(path, OpenRecentCommand));
        }

        OnPropertyChanged(nameof(HasRecentWorkspaces));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsTurkish));
    }
}
