using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Desktop.ViewModels.Import;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Desktop.ViewModels.Training;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
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
    private readonly IImportService _import;
    private readonly IHuggingFaceHub _hub;
    private readonly IExportService _export;
    private readonly IVersionService _versions;
    private readonly IUpdateService _updates;
    private readonly IShellService _shell;
    private readonly RecordOperations _operations;
    private readonly IRecordAnalysisService _analysis;
    private readonly IUserSettings _settings;
    private readonly ITagService _tagService;
    private readonly TagCatalog _tags;
    private readonly AIAssistant _ai;
    private readonly DocumentQuestions _questions;
    private readonly RecordVariations _variations;
    private readonly RecordJudge _judge;
    private readonly ModelEvaluator _evaluator;
    private readonly TrainingLauncher _training;
    private readonly Localizer _localizer;

    public MainWindowViewModel(
        IWorkspaceService workspaces,
        IFileDialogService files,
        IDialogService dialogs,
        IRecordCommandService commands,
        IRecordQueryService queries,
        IImportService import,
        IHuggingFaceHub hub,
        IExportService export,
        IVersionService versions,
        IUpdateService updates,
        IShellService shell,
        RecordOperations operations,
        IRecordAnalysisService analysis,
        IUserSettings settings,
        ITagService tagService,
        TagCatalog tags,
        AIAssistant ai,
        DocumentQuestions questions,
        RecordVariations variations,
        RecordJudge judge,
        ModelEvaluator evaluator,
        TrainingLauncher training,
        Localizer localizer,
        StatusBarViewModel status,
        ExplorerViewModel explorer,
        RecordListViewModel records,
        RecordDetailsViewModel details,
        DatasetOverviewViewModel overview)
    {
        _workspaces = workspaces;
        _files = files;
        _dialogs = dialogs;
        _commands = commands;
        _queries = queries;
        _import = import;
        _hub = hub;
        _export = export;
        _versions = versions;
        _updates = updates;
        _shell = shell;
        _operations = operations;
        _analysis = analysis;
        _settings = settings;
        _tagService = tagService;
        _tags = tags;
        _ai = ai;
        _questions = questions;
        _variations = variations;
        _judge = judge;
        _evaluator = evaluator;
        _training = training;
        _localizer = localizer;
        Status = status;
        Explorer = explorer;
        Records = records;
        Details = details;
        Overview = overview;

        _workspaces.CurrentChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = OnWorkspaceChangedAsync());
        _localizer.CultureChanged += (_, _) => OnLanguageChanged();
        Explorer.PropertyChanged += OnExplorerPropertyChanged;
        Records.SelectionChanged += (_, _) => OnRecordSelectionChanged();
        Records.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecordListViewModel.HasDataset))
            {
                NotifyReviewCommands();
            }
        };
        _operations.Changed += (_, change) => OnRecordsChanged(change);
        _operations.History.PropertyChanged += (_, _) => OnHistoryChanged();
        Overview.AnalysisCompleted += (_, datasetId) => _ = OnAnalysisCompletedAsync(datasetId);
        Overview.NearGroupsChanged += (_, datasetId) => _ = OnNearGroupsChangedAsync(datasetId);
        Overview.ShowRecordsRequested += (_, filter) => Records.ShowQuality(filter);
        Overview.AIConnectionsRequested += (_, _) => ManageAIConnectionsCommand.Execute(null);
        Overview.ExtractQuestionsRequested += (_, _) => ExtractQuestionsCommand.Execute(null);
        Details.AIConnectionsRequested += (_, _) => ManageAIConnectionsCommand.Execute(null);
        Details.VariationsRequested += (_, _) => GenerateVariationsCommand.Execute(null);
        Details.JudgeSelectionRequested += (_, _) => JudgeCommand.Execute(null);
        Details.Judged += (_, recordId) => _ = OnJudgedAsync([recordId]);
        Overview.JudgeRequested += (_, _) => _ = JudgeRestAsync();
        Theme = ThemeService.Parse(settings.UiTheme);
        RefreshRecentWorkspaces();
    }

    public StatusBarViewModel Status { get; }

    public ExplorerViewModel Explorer { get; }

    public RecordListViewModel Records { get; }

    public RecordDetailsViewModel Details { get; }

    /// <summary>
    /// The inspector when no record is selected: quality, tokens and settings of the dataset.
    /// </summary>
    public DatasetOverviewViewModel Overview { get; }

    public ObservableCollection<RecentWorkspaceViewModel> RecentWorkspaces { get; } = [];

    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewWorkspaceCommand), nameof(OpenWorkspaceCommand), nameof(OpenRecentCommand), nameof(CloseWorkspaceCommand), nameof(GenerateSampleDataCommand), nameof(ImportCommand), nameof(ImportFromHubCommand), nameof(NewRecordCommand), nameof(ExportCommand), nameof(ExtractQuestionsCommand), nameof(EvaluateModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(VersionsCommand), nameof(CreateVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(RedoCommand), nameof(EditCustomTermsCommand), nameof(ManageTagsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApproveAndNextCommand), nameof(ArchiveAndNextCommand), nameof(NextRecordCommand), nameof(PreviousRecordCommand), nameof(DeleteSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleTagCommand), nameof(ToggleTagSlotCommand))]
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

    /// <summary>
    /// "Undo Approve #12", or plain "Undo" when there is nothing to undo.
    /// </summary>
    public string UndoHeader => _operations.History.NextUndo is { } action
        ? _localizer.Format("Menu_UndoAction", action.Describe(_localizer))
        : _localizer["Menu_Undo"];

    public string RedoHeader => _operations.History.NextRedo is { } action
        ? _localizer.Format("Menu_RedoAction", action.Describe(_localizer))
        : _localizer["Menu_Redo"];

    private bool CanUndo() => !IsBusy && _operations.History.CanUndo;

    private bool CanReview() => !IsBusy && Records.HasDataset;

    private bool CanRedo() => !IsBusy && _operations.History.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync() => ReplayAsync(undo: true);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task RedoAsync() => ReplayAsync(undo: false);

    /// <summary>
    /// Approves the shown record (saving pending edits) and moves to the next one — Mod+Enter (SDD §6.2).
    /// With several records selected, approves them all.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private async Task ApproveAndNextAsync()
    {
        if (Details.IsBatch)
        {
            await Details.BatchApproveCommand.ExecuteAsync(null);
            return;
        }

        if (Details.HasRecord)
        {
            await Details.ApproveCommand.ExecuteAsync(null);
            await Records.SelectNextAsync();
        }
    }

    /// <summary>
    /// Archives the shown record and moves to the next one — Mod+Shift+Backspace (SDD §6.2).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private async Task ArchiveAndNextAsync()
    {
        if (Details.IsBatch)
        {
            await Details.BatchArchiveCommand.ExecuteAsync(null);
            return;
        }

        if (Details.HasRecord)
        {
            await Details.ArchiveCommand.ExecuteAsync(null);
            await Records.SelectNextAsync();
        }
    }

    /// <summary>
    /// F8: the next record (unsaved edits are saved).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task NextRecordAsync() => Records.SelectNextAsync();

    /// <summary>
    /// Shift+F8: the previous record.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private void PreviousRecord() => Records.SelectPrevious();

    /// <summary>
    /// Delete: the shown record or the selected records, after confirmation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task DeleteSelectionAsync() => Details.IsBatch
        ? Details.BatchDeleteCommand.ExecuteAsync(null)
        : Details.HasRecord ? Details.DeleteCommand.ExecuteAsync(null) : Task.CompletedTask;
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
            await Overview.StopAsync();
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

    private bool CanImport() => !IsBusy && Explorer.SelectedDataset is not null;

    /// <summary>
    /// Raised after Record › New Record shows the new record, so the editor's first field gets the
    /// keyboard.
    /// </summary>
    public event EventHandler? NewRecordShown;

    /// <summary>
    /// Record › New Record (M24): an empty draft of the dataset's type at the end of the list,
    /// written by hand. One step in the history, like any other added records.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task NewRecordAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset || !await Details.SaveIfDirtyAsync())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ids = await _operations.AddAsync(dataset.Id, [new NewRecord(EmptyPayload(dataset.Type), RecordLineage.Manual())], _localizer["History_NewRecordSource"]);
            await ShowAffectedAsync(dataset.Id, ids);
            await Details.ShowAsync(ids[0]);
            Status.Set("Status_RecordCreated", dataset.Name);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
            return;
        }
        finally
        {
            IsBusy = false;
        }

        NewRecordShown?.Invoke(this, EventArgs.Empty);
    }

    // A chat starts with the customer's turn and the answer to write.
    private static RecordPayload EmptyPayload(DatasetType type) => type switch
    {
        DatasetType.Chat => new ChatPayload(null, [new ChatMessage(ChatRole.User, ""), new ChatMessage(ChatRole.Assistant, "")]),
        DatasetType.Preference => new PreferencePayload("", "", ""),
        _ => new InstructionPayload("", "", ""),
    };

    /// <summary>
    /// Picks a file and runs the import wizard for the selected dataset.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset)
        {
            return;
        }

        var path = await _files.PickImportFileAsync(_import.SupportedExtensions);
        if (path is null)
        {
            return;
        }

        await ImportFileAsync(dataset, path);
    }

    /// <summary>
    /// File › Import from Hugging Face (M25): a file of a Hub dataset is downloaded, then imported
    /// through the wizard like any file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFromHubAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset)
        {
            return;
        }

        var hub = new HubImportViewModel(_hub, _localizer, dataset.Name);
        await _dialogs.ShowHubImportAsync(hub);
        if (hub.DownloadedPath is { } path)
        {
            await ImportFileAsync(dataset, path);
        }
    }

    private async Task ImportFileAsync(DatasetNodeViewModel dataset, string path)
    {
        var wizard = new ImportWizardViewModel(_import, _localizer, path, new ImportTarget(dataset.Id, dataset.Name, dataset.Type));
        if (await _dialogs.ShowImportWizardAsync(wizard) is not { } result)
        {
            // The wizard hands a document over to Q&A extraction.
            if (wizard.ExtractQuestionsChosen)
            {
                await ExtractQuestionsAsync(dataset, path);
            }

            return;
        }

        IsBusy = true;
        try
        {
            Explorer.SetRecordCount(dataset.Id, await _queries.CountAsync(dataset.Id, RecordFilter.None));
            if (Records.Dataset?.Id == dataset.Id)
            {
                await Records.ReloadAsync();
                Overview.StartAnalysis();
            }

            Status.Set(result.Skipped > 0 ? "Status_ImportedWithSkipped" : "Status_Imported", result.Imported, dataset.Name, result.Skipped);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExport() => !IsBusy && Explorer.SelectedDataset is not null;

    /// <summary>
    /// Writes the selected dataset, or one of its versions, to training files (SDD §10, §13, §14).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (Explorer.SelectedDataset is not { } dataset || !await Details.SaveIfDirtyAsync())
        {
            return;
        }

        try
        {
            await ExportDatasetAsync(dataset, await _versions.ListAsync(dataset.Id), versionId: null);
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
    }

    private async Task ExportDatasetAsync(DatasetNodeViewModel dataset, IReadOnlyList<VersionInfo> versions, Guid? versionId)
    {
        var tags = await _tagService.ListAsync(dataset.Id);
        var wizard = new ExportWizardViewModel(_export, _files, _localizer, new ExportTarget(dataset.Id, dataset.Name, dataset.Type, versions, versionId, tags));
        if (await _dialogs.ShowExportWizardAsync(wizard) is { } result)
        {
            Status.Set("Status_Exported", result.Exported, dataset.Name, string.Join(", ", result.Files.Select(f => Path.GetFileName(f.Path))));
        }

        // "Start training" on the result screen: the window opens on the folder just written.
        if (wizard.StartTrainingRequested && wizard.TrainingFolder is { } folder && wizard.TrainingTool is { } framework)
        {
            await TrainAsync(new TrainingRunTarget(folder, framework, dataset.Name, dataset.Id, dataset.Type));
        }
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
            Overview.StartAnalysis();
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

    private async Task ReplayAsync(bool undo)
    {
        // Pending edits count as the latest change: they are saved, so undo takes them back first.
        await Details.SaveIfDirtyAsync();
        IsBusy = true;
        try
        {
            var result = undo ? await _operations.UndoAsync() : await _operations.RedoAsync();
            if (result is null)
            {
                return;
            }

            if (result.Count == 0)
            {
                Status.Set("Status_UndoNothing");
                return;
            }

            await ShowAffectedAsync(result.Action.DatasetId, result.Action.RecordIds);
            Status.Set(undo ? "Status_Undone" : "Status_Redone", result.Action.Describe(_localizer));
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Shows the records a change touched: their dataset, reloaded, with them selected.
    private async Task ShowAffectedAsync(Guid datasetId, IReadOnlyList<Guid> recordIds)
    {
        Explorer.SetRecordCount(datasetId, await _queries.CountAsync(datasetId, RecordFilter.None));
        if (Records.Dataset?.Id != datasetId)
        {
            if (Explorer.FindDataset(datasetId) is not { } node)
            {
                return;
            }

            Explorer.SelectedNode = node;
            await Records.Loading;
        }
        else
        {
            await Records.ReloadAsync();
        }

        Records.Select(recordIds);
        // Restored or changed records need fresh analysis results.
        Overview.StartAnalysis();
    }

    /// <summary>
    /// Tools › Personal Data Terms: words to mask besides the built-in rules (SDD §9.1).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseWorkspace))]
    private async Task EditCustomTermsAsync()
    {
        try
        {
            var current = await _analysis.GetCustomTermsAsync();
            var text = await _dialogs.PromptLinesAsync(
                _localizer["Dialog_CustomTermsTitle"],
                _localizer["Dialog_CustomTermsMessage"],
                string.Join(Environment.NewLine, current));
            if (text is null)
            {
                return;
            }

            var terms = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await _analysis.SetCustomTermsAsync(terms);
            Details.CustomTerms = await _analysis.GetCustomTermsAsync();
            Status.Set("Status_CustomTermsSaved", Details.CustomTerms.Count);
            Overview.StartAnalysis();
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
    }

    // Rows keep their place (as after a status change); their scores and the editor's issues follow.
    private async Task OnNearGroupsChangedAsync(Guid datasetId)
    {
        if (Records.Dataset?.Id == datasetId)
        {
            await Records.RefreshAnalysisAsync([.. Records.Items.Select(row => row.Id)]);
            await Details.RefreshQualityAsync();
        }
    }

    private async Task OnAnalysisCompletedAsync(Guid datasetId)
    {
        if (Records.Dataset?.Id == datasetId)
        {
            await Records.RefreshAnalysisAsync();
        }
    }

    // An edited record is analysed right away so its row shows the new tokens and score; its
    // question may also join or leave a group of near-duplicates, which the background run regroups.
    private async Task RefreshAnalysisAsync(Guid recordId)
    {
        try
        {
            await _analysis.AnalyzeRecordsAsync([recordId]);
            await Records.RefreshAnalysisAsync([recordId]);
            Overview.StartAnalysis();
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
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
        _operations.History.Clear();

        Explorer.IsWorkspaceOpen = HasWorkspace;
        EditCustomTermsCommand.NotifyCanExecuteChanged();
        ManageTagsCommand.NotifyCanExecuteChanged();
        if (HasWorkspace)
        {
            try
            {
                // Tags first, so the first rows of the list already show theirs.
                await _tags.ReloadAsync();
                await Explorer.ReloadAsync();
                Details.CustomTerms = await _analysis.GetCustomTermsAsync();
            }
            catch (Exception ex) when (Status.TryReport(ex))
            {
            }
        }
        else
        {
            _tags.Clear();
            Explorer.Clear();
        }
    }

    private void OnExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerViewModel.SelectedDataset) && Explorer.SelectedDataset != Records.Dataset)
        {
            GenerateSampleDataCommand.NotifyCanExecuteChanged();
            ImportCommand.NotifyCanExecuteChanged();
            NewRecordCommand.NotifyCanExecuteChanged();
            ImportFromHubCommand.NotifyCanExecuteChanged();
            ExtractQuestionsCommand.NotifyCanExecuteChanged();
            ExportCommand.NotifyCanExecuteChanged();
            EvaluateModelCommand.NotifyCanExecuteChanged();
            VersionsCommand.NotifyCanExecuteChanged();
            CreateVersionCommand.NotifyCanExecuteChanged();
            Details.Dataset = Explorer.SelectedDataset;
            _ = Details.ShowAsync(null);
            _ = Records.ShowDatasetAsync(Explorer.SelectedDataset);
            _ = Overview.ShowAsync(Explorer.SelectedDataset);
        }
    }

    private void OnRecordSelectionChanged()
    {
        GenerateVariationsCommand.NotifyCanExecuteChanged();
        JudgeCommand.NotifyCanExecuteChanged();
        var rows = Records.SelectedRows;
        _ = rows.Count > 1 && Records.Dataset is { } dataset
            ? Details.ShowBatchAsync(dataset.Id, rows)
            : Details.ShowAsync(rows.FirstOrDefault()?.Id);
        if (rows.Count == 0)
        {
            // The overview comes back into view; statuses or content may have changed meanwhile.
            _ = Overview.RefreshAsync();
        }
    }

    // Keeps the list in step with changes made in the inspector; undo and redo reload instead.
    private void OnRecordsChanged(RecordChange change)
    {
        if (Records.Dataset?.Id != change.DatasetId)
        {
            return;
        }

        switch (change)
        {
            case StatusesChanged statuses:
                foreach (var update in statuses.Changes)
                {
                    Records.UpdateStatus(update.RecordId, update.To);
                }

                break;
            case ContentChanged content:
                Records.UpdateRow(content.RecordId, content.Preview, content.Status);
                _ = RefreshAnalysisAsync(content.RecordId);
                break;
            case RecordsDeleted deleted:
                foreach (var recordId in deleted.RecordIds)
                {
                    Records.Remove(recordId);
                }

                break;
            case TagsChanged tags:
                Records.UpdateTag(tags.RecordIds, tags.TagId, tags.Added);
                break;
        }
    }

    private void NotifyReviewCommands()
    {
        ApproveAndNextCommand.NotifyCanExecuteChanged();
        ArchiveAndNextCommand.NotifyCanExecuteChanged();
        NextRecordCommand.NotifyCanExecuteChanged();
        PreviousRecordCommand.NotifyCanExecuteChanged();
        DeleteSelectionCommand.NotifyCanExecuteChanged();
        ToggleTagCommand.NotifyCanExecuteChanged();
        ToggleTagSlotCommand.NotifyCanExecuteChanged();
        GenerateVariationsCommand.NotifyCanExecuteChanged();
        JudgeCommand.NotifyCanExecuteChanged();
    }

    private void OnHistoryChanged()
    {
        OnPropertyChanged(nameof(UndoHeader));
        OnPropertyChanged(nameof(RedoHeader));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(UndoHeader));
        OnPropertyChanged(nameof(RedoHeader));
    }
}
