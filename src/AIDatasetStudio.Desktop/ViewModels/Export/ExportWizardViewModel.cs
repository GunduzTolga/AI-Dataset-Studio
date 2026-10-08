using System.Collections.ObjectModel;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Export;

/// <summary>
/// Writes a dataset to training files on one screen: choose the format, which records and how
/// to split them against a live preview of the counts and the first lines, then export with
/// progress and review the written files (SDD §10, §13).
/// </summary>
public sealed partial class ExportWizardViewModel : ViewModelBase
{
    public const int SampleLines = 3;

    private readonly IExportService _export;
    private readonly IFileDialogService _files;
    private readonly Localizer _localizer;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _exportCancellation;
    private bool _applying;

    public ExportWizardViewModel(IExportService export, IFileDialogService files, Localizer localizer, ExportTarget target)
    {
        _export = export;
        _files = files;
        _localizer = localizer;
        Target = target;
        Formats = ExportFormats.For(target.Type)
            .Select(format => new FormatOption(
                format,
                ExportText.Format(localizer, format),
                ExportText.Description(localizer, format, target.Type),
                ExportFormats.IsLossy(format, target.Type) ? localizer["Export_AlpacaChatWarning"] : null))
            .ToList();
        SplitModes = Enum.GetValues<SplitMode>().Select(mode => new SplitModeOption(mode, localizer[$"Export_Split{mode}"])).ToList();
        SkipItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSkips));
        _applying = true;
        SelectedFormat = Formats.FirstOrDefault();
        SelectedSplitMode = SplitModes[0];
        _applying = false;
    }

    /// <summary>
    /// Raised when the wizard is finished or cancelled; the window closes.
    /// </summary>
    public event EventHandler? CloseRequested;

    public ExportTarget Target { get; }

    public string TargetText => _localizer.Format("Export_Target", Target.Name, Labels.For(_localizer, Target.Type));

    public IReadOnlyList<FormatOption> Formats { get; }

    public IReadOnlyList<SplitModeOption> SplitModes { get; }

    public ObservableCollection<SkipItem> SkipItems { get; } = [];

    public bool HasSkips => SkipItems.Count > 0;

    public ObservableCollection<string> Samples { get; } = [];

    public ObservableCollection<ExportedFileItem> WrittenFiles { get; } = [];

    /// <summary>
    /// The current preview; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    // ===== Options =====

    [ObservableProperty]
    public partial FormatOption? SelectedFormat { get; set; }

    [ObservableProperty]
    public partial bool IncludeApproved { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeInReview { get; set; }

    [ObservableProperty]
    public partial bool IncludeDraft { get; set; }

    [ObservableProperty]
    public partial bool SkipOverContextLimit { get; set; } = true;

    [ObservableProperty]
    public partial bool SkipDuplicates { get; set; } = true;

    [ObservableProperty]
    public partial bool MaskPersonalData { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplit), nameof(IsCustomSplit))]
    public partial SplitModeOption? SelectedSplitMode { get; set; }

    [ObservableProperty]
    public partial decimal? TrainPercent { get; set; } = 80;

    [ObservableProperty]
    public partial decimal? ValidationPercent { get; set; } = 10;

    [ObservableProperty]
    public partial decimal? TestPercent { get; set; } = 10;

    [ObservableProperty]
    public partial decimal? Seed { get; set; } = SplitOptions.DefaultSeed;

    public bool IsSplit => SelectedSplitMode is { Mode: not SplitMode.None };

    public bool IsCustomSplit => SelectedSplitMode?.Mode == SplitMode.Custom;

    /// <summary>
    /// The options as chosen; <c>null</c> while they are incomplete (see <see cref="ProblemText"/>).
    /// </summary>
    public ExportOptions? Options
    {
        get
        {
            if (SelectedFormat is not { } format || Statuses.Count == 0)
            {
                return null;
            }

            var seed = (int)(Seed ?? SplitOptions.DefaultSeed);
            SplitOptions? split = SelectedSplitMode?.Mode switch
            {
                SplitMode.Standard => SplitOptions.Standard with { Seed = seed },
                SplitMode.TrainValidation => SplitOptions.TrainValidation with { Seed = seed },
                SplitMode.Custom => new SplitOptions((int)(TrainPercent ?? 0), (int)(ValidationPercent ?? 0), (int)(TestPercent ?? 0), seed),
                _ => null,
            };
            return split is { IsValid: false } ? null : new ExportOptions(format.Format, Statuses, SkipOverContextLimit, SkipDuplicates, MaskPersonalData, split);
        }
    }

    public IReadOnlyList<RecordStatus> Statuses
    {
        get
        {
            var statuses = new List<RecordStatus>(3);
            if (IncludeApproved)
            {
                statuses.Add(RecordStatus.Approved);
            }

            if (IncludeInReview)
            {
                statuses.Add(RecordStatus.InReview);
            }

            if (IncludeDraft)
            {
                statuses.Add(RecordStatus.Draft);
            }

            return statuses;
        }
    }

    // ===== Preview =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportedText), nameof(StartText), nameof(SplitEstimateText), nameof(HasSplitEstimate), nameof(ShowNoApprovedHint))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial ExportPreview? Preview { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsPreviewing { get; private set; }

    /// <summary>
    /// Why the options cannot be exported, e.g. no status chosen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? ProblemText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastExport))]
    public partial string? LastExportText { get; private set; }

    public bool HasProblem => ProblemText is not null;

    public bool HasError => ErrorMessage is not null;

    public bool HasNotice => Notice is not null;

    public bool HasLastExport => LastExportText is not null;

    public string ExportedText => ExportText.Count(Preview?.Exported ?? 0);

    public string StartText => _localizer.Format("Export_Start", Preview?.Exported ?? 0);

    /// <summary>
    /// "train ≈ 800 · val ≈ 100 · test ≈ 100": the expected sizes; the exact ones depend on the hashes.
    /// </summary>
    public string SplitEstimateText => Options?.Split is { } split && Preview is { } preview
        ? string.Join(" · ", split.Parts.Select(part => _localizer.Format(
            "Export_PartEstimate", ExportText.Part(_localizer, part), Math.Round(preview.Exported * split.PercentOf(part) / 100.0))))
        : string.Empty;

    public bool HasSplitEstimate => SplitEstimateText.Length > 0;

    /// <summary>
    /// Only approved records are chosen and there are none: say how to include the others.
    /// </summary>
    public bool ShowNoApprovedHint => Preview is { Selected: 0 } && Statuses is [RecordStatus.Approved];

    // ===== Destination =====

    /// <summary>
    /// A folder that already holds split files; waiting for "overwrite" or another folder.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingOverwrite), nameof(OverwriteText))]
    public partial string? PendingFolder { get; private set; }

    public bool IsConfirmingOverwrite => PendingFolder is not null;

    public string OverwriteText => PendingFolder is { } folder && Options is { } options
        ? _localizer.Format("Export_OverwriteQuestion", string.Join(", ", ExistingFiles(options, folder).Select(Path.GetFileName)), folder)
        : string.Empty;

    // ===== Export =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsSetup), nameof(ResultText))]
    public partial ExportResult? Result { get; private set; }

    public bool IsSetup => !IsExporting && !IsDone;

    public bool IsDone => Result is not null;

    public string ResultText => Result is { } r ? _localizer.Format("Export_DoneSummary", r.Exported, Target.Name) : string.Empty;

    /// <summary>
    /// Restores the dataset's last export settings and shows the preview.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (await _export.GetLastOptionsAsync(Target.DatasetId) is { } last)
            {
                Apply(last);
            }

            if (await _export.GetLastExportAsync(Target.DatasetId) is { } previous)
            {
                LastExportText = _localizer.Format(
                    "Export_LastExport",
                    previous.ExportedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                    ExportText.Format(_localizer, previous.Format),
                    previous.RecordCount);
            }
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }

        await RefreshAsync();
    }

    private bool CanExport() => IsSetup && !IsPreviewing && Options is not null && Preview is { Exported: > 0 };

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (Options is not { } options)
        {
            return;
        }

        PendingFolder = null;
        if (options.Split is null)
        {
            if (await _files.PickExportFileAsync(SuggestedFileName(Target.Name)) is { } file)
            {
                await RunExportAsync(options, file);
            }

            return;
        }

        if (await _files.PickExportFolderAsync() is not { } folder)
        {
            return;
        }

        if (ExistingFiles(options, folder).Count > 0)
        {
            PendingFolder = folder; // asked in the wizard: overwrite or choose another folder
            return;
        }

        await RunExportAsync(options, folder);
    }

    [RelayCommand]
    private async Task ConfirmOverwriteAsync()
    {
        if (PendingFolder is { } folder && Options is { } options)
        {
            PendingFolder = null;
            await RunExportAsync(options, folder);
        }
    }

    [RelayCommand]
    private void CancelOverwrite() => PendingFolder = null;

    /// <summary>
    /// Stops a running export (nothing is written), or closes the wizard.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_exportCancellation is { } cancellation)
        {
            cancellation.Cancel();
            return;
        }

        _work?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task RunExportAsync(ExportOptions options, string destination)
    {
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        Notice = null;
        ErrorMessage = null;
        var total = Preview?.Selected ?? 0;
        ProgressValue = 0;
        ProgressText = _localizer.Format("Export_Progress", 0, total);
        IsExporting = true;
        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : Math.Min(1, (double)p.Processed / p.Total);
            ProgressText = _localizer.Format("Export_Progress", p.Processed, p.Total);
        });

        try
        {
            var result = await _export.ExportAsync(Target.DatasetId, options, destination, progress, cancellation.Token);
            WrittenFiles.Clear();
            foreach (var file in result.Files)
            {
                WrittenFiles.Add(new ExportedFileItem(
                    Path.GetFileName(file.Path),
                    Path.GetDirectoryName(file.Path) ?? string.Empty,
                    _localizer.Format("Export_FileCount", file.Count)));
            }

            SkipItems.Clear();
            AddSkips(result.Unsupported, result.OverContextLimit, result.Duplicates, notAnalyzed: 0);
            if (result.Masked > 0)
            {
                SkipItems.Add(new SkipItem(_localizer["Export_Masked"], ExportText.Count(result.Masked), IsInfo: true));
            }

            Result = result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Notice = _localizer["Export_Cancelled"];
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }
        finally
        {
            _exportCancellation = null;
            IsExporting = false;
        }
    }

    partial void OnSelectedFormatChanged(FormatOption? value) => OnOptionChanged();

    partial void OnIncludeApprovedChanged(bool value) => OnOptionChanged();

    partial void OnIncludeInReviewChanged(bool value) => OnOptionChanged();

    partial void OnIncludeDraftChanged(bool value) => OnOptionChanged();

    partial void OnSkipOverContextLimitChanged(bool value) => OnOptionChanged();

    partial void OnSkipDuplicatesChanged(bool value) => OnOptionChanged();

    partial void OnMaskPersonalDataChanged(bool value) => OnOptionChanged();

    partial void OnSelectedSplitModeChanged(SplitModeOption? value) => OnOptionChanged();

    partial void OnTrainPercentChanged(decimal? value) => OnOptionChanged();

    partial void OnValidationPercentChanged(decimal? value) => OnOptionChanged();

    partial void OnTestPercentChanged(decimal? value) => OnOptionChanged();

    partial void OnSeedChanged(decimal? value) => OnOptionChanged();

    private void OnOptionChanged()
    {
        if (!_applying)
        {
            PendingFolder = null;
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Recounts and renders the first lines for the current options; a newer call replaces an older one.
    /// </summary>
    private Task RefreshAsync()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        PendingWork = RunCore();
        return PendingWork;

        async Task RunCore()
        {
            ExportCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SplitEstimateText));
            OnPropertyChanged(nameof(HasSplitEstimate));
            if (Options is not { } options)
            {
                ProblemText = Statuses.Count == 0 ? _localizer["Export_NoStatus"] : _localizer["Export_InvalidSplit"];
                Preview = null;
                Samples.Clear();
                SkipItems.Clear();
                return;
            }

            ProblemText = null;
            IsPreviewing = true;
            try
            {
                var preview = await _export.PreviewAsync(Target.DatasetId, options, SampleLines, token);
                token.ThrowIfCancellationRequested();
                Preview = preview;
                Samples.Clear();
                foreach (var line in preview.SampleLines)
                {
                    Samples.Add(line);
                }

                SkipItems.Clear();
                AddSkips(preview.Unsupported, preview.OverContextLimit, preview.Duplicates, preview.NotAnalyzed);
                ErrorMessage = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Replaced by newer options.
            }
            catch (Exception ex) when (Describe(ex) is { } message)
            {
                ErrorMessage = message;
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsPreviewing = false;
                }
            }
        }
    }

    private void AddSkips(long unsupported, long overLimit, long duplicates, long notAnalyzed)
    {
        if (unsupported > 0)
        {
            SkipItems.Add(new SkipItem(_localizer["Export_SkipUnsupported"], ExportText.Count(unsupported)));
        }

        if (overLimit > 0)
        {
            SkipItems.Add(new SkipItem(_localizer["Export_SkipOverLimit"], ExportText.Count(overLimit)));
        }

        if (duplicates > 0)
        {
            SkipItems.Add(new SkipItem(_localizer["Export_SkipDuplicates"], ExportText.Count(duplicates)));
        }

        if (notAnalyzed > 0)
        {
            SkipItems.Add(new SkipItem(_localizer["Export_NotAnalyzed"], ExportText.Count(notAnalyzed), IsInfo: true));
        }
    }

    private void Apply(ExportOptions options)
    {
        _applying = true;
        try
        {
            SelectedFormat = Formats.FirstOrDefault(f => f.Format == options.Format) ?? SelectedFormat;
            IncludeApproved = options.Statuses.Contains(RecordStatus.Approved);
            IncludeInReview = options.Statuses.Contains(RecordStatus.InReview);
            IncludeDraft = options.Statuses.Contains(RecordStatus.Draft);
            SkipOverContextLimit = options.SkipOverContextLimit;
            SkipDuplicates = options.SkipDuplicates;
            MaskPersonalData = options.MaskPersonalData;
            var mode = options.Split switch
            {
                null => SplitMode.None,
                var s when s with { Seed = SplitOptions.DefaultSeed } == SplitOptions.Standard => SplitMode.Standard,
                var s when s with { Seed = SplitOptions.DefaultSeed } == SplitOptions.TrainValidation => SplitMode.TrainValidation,
                _ => SplitMode.Custom,
            };
            SelectedSplitMode = SplitModes.First(m => m.Mode == mode);
            if (options.Split is { } split)
            {
                (TrainPercent, ValidationPercent, TestPercent, Seed) = (split.TrainPercent, split.ValidationPercent, split.TestPercent, split.Seed);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private static List<string> ExistingFiles(ExportOptions options, string folder) =>
        [.. ExportFormats.Files(options, folder).Select(f => f.Path).Where(File.Exists)];

    /// <summary>
    /// The dataset name as a file name: characters no file system allows are replaced.
    /// </summary>
    public static string SuggestedFileName(string datasetName)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var name = new string([.. datasetName.Select(c => invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.');
        return (name.Length == 0 ? "dataset" : name) + ExportFormats.Extension;
    }

    private string? Describe(Exception exception) =>
        UiErrors.TryDescribe(exception) is { } error ? _localizer.Format(error.Key, error.Args) : null;
}
