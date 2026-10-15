using System.Collections.ObjectModel;
using AIDatasetStudio.Application.AI;
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
        // Only tags the dataset uses can form strata; groups come from "group:value" names.
        var used = (target.Tags ?? []).Where(t => t.RecordCount > 0).ToList();
        StratifyOptions =
        [
            new StratifyOption(null, localizer["Export_StratifyNone"]),
            .. used.Count > 0 ? [new StratifyOption(SplitOptions.AnyTag, localizer["Export_StratifyAnyTag"])] : Array.Empty<StratifyOption>(),
            .. used.Select(t => t.Group).OfType<string>().DistinctBy(Domain.Entities.Tag.FoldName).Order(StringComparer.CurrentCulture)
                .Select(group => new StratifyOption(group, localizer.Format("Export_StratifyGroup", group))),
        ];
        Sources =
        [
            new SourceOption(null, localizer["Export_SourceCurrent"]),
            .. (target.Versions ?? []).Select(v => new SourceOption(v, localizer.Format("Export_SourceVersion", v.Number, v.CreatedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)))),
        ];
        TrainingChoices =
        [
            new TrainingChoice(null, localizer["Export_TrainingNone"]),
            .. Enum.GetValues<TrainingFramework>().Select(framework => new TrainingChoice(framework, localizer[$"Export_Training{framework}"])),
        ];
        GpuChoices = [.. Enum.GetValues<GpuVendor>().Select(vendor => new GpuChoice(vendor, localizer[$"Export_Gpu{vendor}"]))];
        PlatformChoices = [.. Enum.GetValues<TrainingPlatform>().Select(platform => new PlatformChoice(platform, localizer[$"Export_Platform{platform}"]))];
        GpuMemoryChoices =
        [
            new GpuMemoryChoice(null, localizer["Export_GpuMemoryUnknown"]),
            .. GpuMemorySizes.Select(gb => new GpuMemoryChoice(gb, localizer.Format("Export_GpuMemoryGb", gb))),
        ];
        SkipItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSkips));
        _applying = true;
        SelectedTraining = TrainingChoices[0];
        SelectedGpu = GpuChoices[0];

        SelectedPlatform = PlatformChoices.First(c => c.Platform == TrainingConfigs.DefaultPlatform);
        SelectedGpuMemory = GpuMemoryChoices[0];
        SelectedFormat = Formats.FirstOrDefault();
        SelectedSplitMode = SplitModes[0];
        SelectedStratify = StratifyOptions[0];
        SelectedSource = Sources.FirstOrDefault(s => s.VersionId == target.VersionId) ?? Sources[0];
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

    /// <summary>
    /// No strata, by tag, then one entry per tag group the dataset uses.
    /// </summary>
    public IReadOnlyList<StratifyOption> StratifyOptions { get; }

    /// <summary>
    /// The dataset has tags, so a split can be made within them.
    /// </summary>
    public bool CanStratify => StratifyOptions.Count > 1;

    /// <summary>
    /// The current records, then the versions newest first.
    /// </summary>
    public IReadOnlyList<SourceOption> Sources { get; }

    public bool HasVersions => Sources.Count > 1;

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
    [NotifyPropertyChangedFor(nameof(SourceHint), nameof(HasSourceHint))]
    public partial SourceOption? SelectedSource { get; set; }

    /// <summary>
    /// For a version: it is written as frozen, checked against its own context length.
    /// </summary>
    public string? SourceHint => SelectedSource?.Version is { } version
        ? _localizer.Format("Export_SourceVersionHint", version.Number, version.Tokenizer, version.ContextLength)
        : null;

    public bool HasSourceHint => SourceHint is not null;

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

    /// <summary>
    /// Leave out records the model scored 1–2 (SDD §17.7); off unless chosen.
    /// </summary>
    [ObservableProperty]
    public partial bool SkipLowJudgeScore { get; set; }

    /// <summary>
    /// Whether records of this type can be judged, and so left out for a low score.
    /// </summary>
    public bool CanSkipLowJudgeScore => Judging.Supports(Target.Type);

    [ObservableProperty]
    public partial bool MaskPersonalData { get; set; } = true;

    /// <summary>
    /// Write the reasoning of answers that have it (ADR-052); off to train a model that answers
    /// without thinking first.
    /// </summary>
    [ObservableProperty]
    public partial bool IncludeReasoning { get; set; } = true;

    /// <summary>
    /// Some chosen records have reasoning, so whether to write it is asked.
    /// </summary>
    public bool HasReasoning => Preview is { WithReasoning: > 0 };

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStratified), nameof(SplitHint))]
    public partial StratifyOption? SelectedStratify { get; set; }

    public bool IsStratified => SelectedStratify?.StratifyBy is not null;

    /// <summary>
    /// How records are placed: by hash alone, or cut to exact shares within each stratum.
    /// </summary>
    public string SplitHint => _localizer[IsStratified ? "Export_StratifyHint" : "Export_SplitHint"];

    public bool IsSplit => SelectedSplitMode is { Mode: not SplitMode.None };

    // ===== Training configuration (M20) =====

    public IReadOnlyList<TrainingChoice> TrainingChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTraining), nameof(TrainingHint))]
    public partial TrainingChoice? SelectedTraining { get; set; }

    /// <summary>
    /// The Hugging Face id of the model to fine-tune; follows the format until the user types one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText), nameof(MemoryFitLevel), nameof(IsMemoryWarning))]
    public partial string BaseModel { get; set; } = TrainingConfigs.DefaultBaseModel(ExportFormat.OpenAi);

    /// <summary>
    /// QLoRA instead of LoRA: the model is loaded in 4 bits (M26).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText), nameof(MemoryFitLevel), nameof(IsMemoryWarning))]
    public partial bool UseQLoRA { get; set; }

    // ===== The GPU that trains (M27) =====

    /// <summary>The memory sizes offered, in GB: laptop GPUs to data-center ones.</summary>
    public static IReadOnlyList<int> GpuMemorySizes { get; } = [4, 6, 8, 10, 12, 16, 20, 24, 32, 48, 80];

    public IReadOnlyList<GpuChoice> GpuChoices { get; }

    public IReadOnlyList<GpuMemoryChoice> GpuMemoryChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlatformHint), nameof(HasPlatformHint))]
    public partial GpuChoice? SelectedGpu { get; set; }

    public IReadOnlyList<PlatformChoice> PlatformChoices { get; }

    /// <summary>
    /// The system that trains (M29): Windows gets settings that run there.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlatformHint), nameof(HasPlatformHint), nameof(IsWindows), nameof(SaveMemoryLabel), nameof(SaveMemoryHint), nameof(MemoryText), nameof(MemoryFitLevel), nameof(IsMemoryWarning))]
    public partial PlatformChoice? SelectedPlatform { get; set; }

    public bool IsWindows => SelectedPlatform?.Platform == TrainingPlatform.Windows;

    /// <summary>
    /// What memory saving does where it trains: on Windows only the activations move to RAM.
    /// </summary>
    public string SaveMemoryLabel => _localizer[IsWindows ? "Export_SaveMemoryWindows" : "Export_SaveMemory"];

    public string SaveMemoryHint => _localizer[IsWindows ? "Export_SaveMemoryWindowsHint" : "Export_SaveMemoryHint"];

    /// <summary>
    /// What to install for the chosen system and GPU, and what does not run there.
    /// </summary>
    public string? PlatformHint => (SelectedPlatform?.Platform, SelectedGpu?.Vendor) switch
    {
        (TrainingPlatform.Windows, GpuVendor.Amd) => _localizer["Export_HintWindowsAmd"],
        (TrainingPlatform.Windows, _) => _localizer["Export_HintWindowsNvidia"],
        (_, GpuVendor.Amd) => _localizer["Export_GpuAmdHint"],
        _ => null,
    };

    public bool HasPlatformHint => PlatformHint is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText), nameof(MemoryFitLevel), nameof(IsMemoryWarning))]
    public partial GpuMemoryChoice? SelectedGpuMemory { get; set; }

    /// <summary>
    /// Liger kernels and activations in the computer's memory: the same training in less GPU memory.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryText), nameof(MemoryFitLevel), nameof(IsMemoryWarning))]
    public partial bool SaveMemory { get; set; }

    /// <summary>
    /// How the run sits in the chosen GPU's memory; <c>null</c> without a GPU size or a model size.
    /// </summary>
    public MemoryFit? MemoryFitLevel =>
        SelectedGpuMemory?.Gigabytes is { } memory && TrainingMemory.ParameterBillions(BaseModel) is { } size
            ? TrainingMemory.Fit(TrainingMemory.EstimateGb(size, UseQLoRA, SaveMemory, IsWindows), memory)
            : null;

    public bool IsMemoryWarning => MemoryFitLevel is MemoryFit.Tight or MemoryFit.TooBig;

    /// <summary>
    /// The memory the run needs against the GPU's, and what to change when it does not fit.
    /// </summary>
    public string MemoryText
    {
        get
        {
            var size = TrainingMemory.ParameterBillions(BaseModel);
            var estimate = size is { } billions ? TrainingMemory.EstimateGb(billions, UseQLoRA, SaveMemory, IsWindows) : (double?)null;
            if (SelectedGpuMemory?.Gigabytes is not { } memory)
            {
                return estimate is { } gb ? _localizer.Format("Export_MemoryEstimate", gb, TrainingMemory.EstimatedSequenceLength) : string.Empty;
            }

            var largest = TrainingMemory.LargestFitting(memory, IsWindows) is { } fitting
                ? _localizer.Format("Export_MemoryLargest", fitting)
                : _localizer["Export_MemoryTooSmall"];
            if (estimate is not { } needed)
            {
                return _localizer["Export_MemoryUnknownSize"] + " " + largest;
            }

            return TrainingMemory.Fit(needed, memory) switch
            {
                MemoryFit.Fits => _localizer.Format("Export_MemoryFits", needed, memory),
                MemoryFit.Tight => _localizer.Format("Export_MemoryTight", needed, memory),
                _ when !UseQLoRA || !SaveMemory => _localizer.Format("Export_MemoryTooBig", needed, memory) + " " + _localizer["Export_MemoryTurnOn"],
                _ => _localizer.Format("Export_MemoryTooBig", needed, memory) + " " + largest,
            };
        }
    }

    public bool IsTraining => SelectedTraining?.Framework is not null;

    /// <summary>
    /// Which files are written next to the data and the command that runs them.
    /// </summary>
    public string TrainingHint => SelectedTraining?.Framework switch
    {
        TrainingFramework.Axolotl => _localizer["Export_TrainingAxolotlHint"],
        TrainingFramework.LlamaFactory => _localizer["Export_TrainingLlamaFactoryHint"],
        _ => string.Empty,
    };

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
            var stratifyBy = SelectedStratify?.StratifyBy;
            SplitOptions? split = SelectedSplitMode?.Mode switch
            {
                SplitMode.Standard => SplitOptions.Standard with { Seed = seed, StratifyBy = stratifyBy },
                SplitMode.TrainValidation => SplitOptions.TrainValidation with { Seed = seed, StratifyBy = stratifyBy },
                SplitMode.Custom => new SplitOptions((int)(TrainPercent ?? 0), (int)(ValidationPercent ?? 0), (int)(TestPercent ?? 0), seed, stratifyBy),
                _ => null,
            };
            var training = SelectedTraining?.Framework is { } framework
                ? new TrainingOptions(
                    framework, BaseModel.Trim(), UseQLoRA, SelectedGpu?.Vendor ?? GpuVendor.Nvidia, SelectedGpuMemory?.Gigabytes, SaveMemory, SelectedPlatform?.Platform ?? TrainingPlatform.Linux)
                : null;
            if (split is { IsValid: false }
                || (training is not null && (!TrainingConfigs.Supports(training.Framework, format.Format, Target.Type) || !TrainingConfigs.Supports(training.Framework, training.Platform))))
            {
                return null;
            }

            return new ExportOptions(
                format.Format, Statuses, SkipOverContextLimit, SkipDuplicates, MaskPersonalData, split, training, SkipLowJudgeScore && CanSkipLowJudgeScore,
                IncludeReasoning);
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
    [NotifyPropertyChangedFor(nameof(HasReasoning))]
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

    public string ResultText => Result is { } r
        ? _localizer.Format("Export_DoneSummary", r.Exported, SelectedSource?.Version is { } version ? $"{Target.Name} v{version.Number}" : Target.Name)
        : string.Empty;

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
                    previous.VersionNumber is null ? "Export_LastExport" : "Export_LastExportVersion",
                    previous.ExportedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                    ExportText.Format(_localizer, previous.Format),
                    previous.RecordCount,
                    previous.VersionNumber);
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
            var name = SelectedSource?.Version is { } version ? $"{Target.Name} v{version.Number}" : Target.Name;
            if (await _files.PickExportFileAsync(SuggestedFileName(name)) is { } file)
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

    /// <summary>
    /// The folder the training configuration went to; <c>null</c> when none was written.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTrain))]
    public partial string? TrainingFolder { get; private set; }

    /// <summary>
    /// The tool the configuration was written for.
    /// </summary>
    public TrainingFramework? TrainingTool { get; private set; }

    public bool CanTrain => TrainingFolder is not null;

    /// <summary>
    /// "Start training" was chosen: once the wizard closes, the training window opens on the folder (M28).
    /// </summary>
    public bool StartTrainingRequested { get; private set; }

    [RelayCommand]
    private void StartTraining()
    {
        StartTrainingRequested = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

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
            var result = await _export.ExportAsync(Target.DatasetId, options, destination, progress, SelectedSource?.VersionId, cancellation.Token);
            WrittenFiles.Clear();
            foreach (var file in result.Files)
            {
                WrittenFiles.Add(new ExportedFileItem(
                    Path.GetFileName(file.Path),
                    Path.GetDirectoryName(file.Path) ?? string.Empty,
                    _localizer.Format("Export_FileCount", file.Count)));
            }

            foreach (var file in result.TrainingFiles ?? [])
            {
                WrittenFiles.Add(new ExportedFileItem(Path.GetFileName(file), Path.GetDirectoryName(file) ?? string.Empty, _localizer["Export_TrainingFile"]));
            }

            TrainingTool = result.TrainingFiles is { Count: > 0 } ? options.Training?.Framework : null;
            TrainingFolder = TrainingTool is not null ? Path.GetDirectoryName(result.TrainingFiles![0]) : null;

            SkipItems.Clear();
            AddSkips(result.Unsupported, result.OverContextLimit, result.LowJudgeScore, result.Duplicates, notAnalyzed: 0);
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

    partial void OnSelectedSourceChanged(SourceOption? value) => OnOptionChanged();

    partial void OnSelectedFormatChanged(FormatOption? oldValue, FormatOption? newValue)
    {
        // The suggested model follows the format's chat template unless the user typed their own.
        if (oldValue is not null && newValue is not null && BaseModel.Trim() == TrainingConfigs.DefaultBaseModel(oldValue.Format))
        {
            BaseModel = TrainingConfigs.DefaultBaseModel(newValue.Format);
        }

        OnOptionChanged();
    }

    partial void OnSelectedTrainingChanged(TrainingChoice? value) => OnOptionChanged();

    partial void OnBaseModelChanged(string value) => OnOptionChanged();

    partial void OnUseQLoRAChanged(bool value) => OnOptionChanged();

    partial void OnSelectedGpuChanged(GpuChoice? value) => OnOptionChanged();

    partial void OnSelectedPlatformChanged(PlatformChoice? value) => OnOptionChanged();

    partial void OnSelectedGpuMemoryChanged(GpuMemoryChoice? value) => OnOptionChanged();

    partial void OnSaveMemoryChanged(bool value) => OnOptionChanged();

    partial void OnIncludeApprovedChanged(bool value) => OnOptionChanged();

    partial void OnIncludeInReviewChanged(bool value) => OnOptionChanged();

    partial void OnIncludeDraftChanged(bool value) => OnOptionChanged();

    partial void OnSkipOverContextLimitChanged(bool value) => OnOptionChanged();

    partial void OnSkipDuplicatesChanged(bool value) => OnOptionChanged();

    partial void OnSkipLowJudgeScoreChanged(bool value) => OnOptionChanged();

    partial void OnMaskPersonalDataChanged(bool value) => OnOptionChanged();

    partial void OnIncludeReasoningChanged(bool value) => OnOptionChanged();

    partial void OnSelectedSplitModeChanged(SplitModeOption? value) => OnOptionChanged();

    partial void OnTrainPercentChanged(decimal? value) => OnOptionChanged();

    partial void OnValidationPercentChanged(decimal? value) => OnOptionChanged();

    partial void OnTestPercentChanged(decimal? value) => OnOptionChanged();

    partial void OnSeedChanged(decimal? value) => OnOptionChanged();

    partial void OnSelectedStratifyChanged(StratifyOption? value) => OnOptionChanged();

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
                ProblemText = Statuses.Count == 0 ? _localizer["Export_NoStatus"]
                    : SelectedTraining?.Framework is { } framework && SelectedFormat is { } format && !TrainingConfigs.Supports(framework, format.Format, Target.Type)
                        ? _localizer.Format("Export_TrainingUnsupported", SelectedTraining.Label, format.Label)
                    : SelectedTraining?.Framework is { } tool && SelectedPlatform is { } platform && !TrainingConfigs.Supports(tool, platform.Platform)
                        ? _localizer.Format("Export_TrainingUnsupportedPlatform", SelectedTraining.Label, platform.Label)
                        : _localizer["Export_InvalidSplit"];
                Preview = null;
                Samples.Clear();
                SkipItems.Clear();
                return;
            }

            ProblemText = null;
            IsPreviewing = true;
            try
            {
                var preview = await _export.PreviewAsync(Target.DatasetId, options, SampleLines, SelectedSource?.VersionId, token);
                token.ThrowIfCancellationRequested();
                Preview = preview;
                Samples.Clear();
                foreach (var line in preview.SampleLines)
                {
                    Samples.Add(line);
                }

                SkipItems.Clear();
                AddSkips(preview.Unsupported, preview.OverContextLimit, preview.LowJudgeScore, preview.Duplicates, preview.NotAnalyzed);
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

    private void AddSkips(long unsupported, long overLimit, long lowScore, long duplicates, long notAnalyzed)
    {
        if (unsupported > 0)
        {
            SkipItems.Add(new SkipItem(_localizer["Export_SkipUnsupported"], ExportText.Count(unsupported)));
        }

        if (overLimit > 0)
        {
            SkipItems.Add(new SkipItem(_localizer["Export_SkipOverLimit"], ExportText.Count(overLimit)));
        }

        if (lowScore > 0)
        {
            SkipItems.Add(new SkipItem(_localizer["Export_SkipLowJudgeScore"], ExportText.Count(lowScore)));
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
            SkipLowJudgeScore = options.SkipLowJudgeScore;
            MaskPersonalData = options.MaskPersonalData;
            IncludeReasoning = options.IncludeReasoning;
            SelectedTraining = TrainingChoices.First(c => c.Framework == options.Training?.Framework);
            UseQLoRA = options.Training?.QLoRA == true;
            SaveMemory = options.Training?.SaveMemory == true;
            SelectedGpu = GpuChoices.FirstOrDefault(c => c.Vendor == options.Training?.Gpu) ?? GpuChoices[0];
            if (options.Training is { } remembered)
            {
                SelectedPlatform = PlatformChoices.First(c => c.Platform == remembered.Platform);
            }
            SelectedGpuMemory = GpuMemoryChoices.FirstOrDefault(c => c.Gigabytes == options.Training?.GpuMemoryGb) ?? GpuMemoryChoices[0];
            if (options.Training is { BaseModel.Length: > 0 } training)
            {
                BaseModel = training.BaseModel;
            }

            // The shares decide the mode; seed and strata are set apart.
            var shares = options.Split is { } chosen ? chosen with { Seed = SplitOptions.DefaultSeed, StratifyBy = null } : null;
            var mode = shares switch
            {
                null => SplitMode.None,
                var s when s == SplitOptions.Standard => SplitMode.Standard,
                var s when s == SplitOptions.TrainValidation => SplitMode.TrainValidation,
                _ => SplitMode.Custom,
            };
            SelectedSplitMode = SplitModes.First(m => m.Mode == mode);
            if (options.Split is { } split)
            {
                (TrainPercent, ValidationPercent, TestPercent, Seed) = (split.TrainPercent, split.ValidationPercent, split.TestPercent, split.Seed);

                // A group the dataset no longer uses is dropped (the split stays plain).
                SelectedStratify = StratifyOptions.FirstOrDefault(o => o.StratifyBy is { } by && split.StratifyBy is { } last
                    && Domain.Entities.Tag.FoldName(by) == Domain.Entities.Tag.FoldName(last)) ?? StratifyOptions[0];
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private static List<string> ExistingFiles(ExportOptions options, string folder) =>
        [
            .. ExportFormats.Files(options, folder).Select(f => f.Path)
                .Concat(options.Training is { } training ? TrainingConfigs.FileNames(training.Framework).Select(name => Path.Combine(folder, name)) : [])
                .Where(File.Exists),
        ];

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
