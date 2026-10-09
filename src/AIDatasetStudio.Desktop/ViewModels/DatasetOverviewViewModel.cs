using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// One bar of the token histogram.
/// </summary>
/// <param name="Fraction">Share of the largest bucket, 0–1, for the bar width.</param>
public sealed record HistogramBar(string Label, long Count, double Fraction, bool IsOverLimit)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// A rule with records that break it, with what can be done about it (SDD §8.3).
/// </summary>
public sealed record IssueSummaryItem(QualityRule Rule, IssueSeverity Severity, string Label, long Count, QualityFilter Filter, bool CanArchive)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    public bool IsError => Severity == IssueSeverity.Error;

    public bool IsWarning => Severity == IssueSeverity.Warning;
}

public sealed record TokenizerOption(string Id, string Label);

/// <summary>
/// A context length to choose; the label follows the UI language.
/// </summary>
public sealed partial class ContextLengthOption(int value, string label) : ObservableObject
{
    public int Value { get; } = value;

    [ObservableProperty]
    public partial string Label { get; set; } = label;
}

/// <summary>
/// The inspector when no record is selected: the dataset's quality score, token statistics and
/// issues with clean-up actions, its tokenizer settings and what the AI should keep in mind for
/// it. Also runs the background analysis that keeps token counts and rule results up to date
/// (SDD §7, §8, §17).
/// </summary>
public sealed partial class DatasetOverviewViewModel : ViewModelBase
{
    private readonly IRecordAnalysisService _analysis;
    private readonly ICatalogService _catalog;
    private readonly RecordOperations _operations;
    private readonly IDialogService _dialogs;
    private readonly StatusBarViewModel _status;
    private readonly AIAssistant _ai;
    private readonly IJudgmentService _judgments;
    private readonly ISemanticIndex _semantic;
    private readonly Localizer _localizer;
    private CancellationTokenSource? _guidanceDelay;
    private CancellationTokenSource? _running;
    private Task _runningTask = Task.CompletedTask;
    private bool _applyingSettings;

    public DatasetOverviewViewModel(
        IRecordAnalysisService analysis,
        ICatalogService catalog,
        ITokenizerProvider tokenizers,
        RecordOperations operations,
        IDialogService dialogs,
        StatusBarViewModel status,
        AIAssistant ai,
        IJudgmentService judgments,
        ISemanticIndex semantic,
        Localizer localizer)
    {
        _analysis = analysis;
        _catalog = catalog;
        _operations = operations;
        _dialogs = dialogs;
        _status = status;
        _ai = ai;
        _judgments = judgments;
        _semantic = semantic;
        _localizer = localizer;
        _ai.ConnectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AIConnectionText));
            OnPropertyChanged(nameof(HasAIConnection));
            RefreshSemanticTexts();
        };
        TokenizerOptions = tokenizers.Available.Select(id => new TokenizerOption(id, TokenizerLabel(id))).ToList();
        ContextOptions = Tokenizers.ContextLengths.Select(n => new ContextLengthOption(n, ContextLabel(n))).ToList();
        _localizer.CultureChanged += (_, _) => Refresh();
    }

    /// <summary>
    /// Raised after an analysis run stored new results, so lists can show them.
    /// </summary>
    public event EventHandler<Guid>? AnalysisCompleted;

    /// <summary>
    /// Raised when only the groups of near-duplicate questions changed (after an edit): scores of
    /// the shown records may differ, but the list should not reload under the user.
    /// </summary>
    public event EventHandler<Guid>? NearGroupsChanged;

    /// <summary>
    /// Raised when the user asks to see the records with an issue.
    /// </summary>
    public event EventHandler<QualityFilter>? ShowRecordsRequested;

    /// <summary>
    /// Raised when the user wants to set up or change the AI connection.
    /// </summary>
    public event EventHandler? AIConnectionsRequested;

    /// <summary>
    /// Raised when the user wants the model to write questions from a document into this dataset.
    /// </summary>
    public event EventHandler? ExtractQuestionsRequested;

    /// <summary>
    /// How long typing pauses before the AI instructions are saved.
    /// </summary>
    public static TimeSpan GuidanceSaveDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    public IReadOnlyList<TokenizerOption> TokenizerOptions { get; }

    public IReadOnlyList<ContextLengthOption> ContextOptions { get; }

    public ObservableCollection<HistogramBar> Histogram { get; } = [];

    public ObservableCollection<IssueSummaryItem> Issues { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataset))]
    public partial DatasetNodeViewModel? Dataset { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary), nameof(ScoreText), nameof(Tone), nameof(IsGood), nameof(IsFair), nameof(IsPoor))]
    [NotifyPropertyChangedFor(nameof(AnalyzedText), nameof(TotalTokensText), nameof(MaxTokensText), nameof(OverLimitText), nameof(HasOverLimit))]
    [NotifyPropertyChangedFor(nameof(HasIssues), nameof(ShowNoIssues), nameof(HasRecords))]
    public partial DatasetQualitySummary? Summary { get; private set; }

    [ObservableProperty]
    public partial TokenizerOption? SelectedTokenizer { get; set; }

    [ObservableProperty]
    public partial ContextLengthOption? SelectedContextLength { get; set; }

    /// <summary>
    /// What the AI should keep in mind for this dataset; saved shortly after typing stops.
    /// </summary>
    [ObservableProperty]
    public partial string? AIGuidance { get; set; }

    /// <summary>
    /// The pending save of <see cref="AIGuidance"/>; awaited by tests.
    /// </summary>
    public Task GuidanceSaving { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// "qwen2.5:7b · Ollama", or that no connection is set up.
    /// </summary>
    public string AIConnectionText => _ai.Connection is { } connection ? AIText.Describe(connection) : _localizer["Overview_NoAIConnection"];

    public bool HasAIConnection => _ai.Connection is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial bool IsAnalyzing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressValue))]
    public partial long AnalysisDone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressValue))]
    public partial long AnalysisTotal { get; private set; }

    public bool HasDataset => Dataset is not null;

    public bool HasSummary => Summary is not null;

    public bool HasRecords => Summary is { RecordCount: > 0 };

    public bool HasIssues => Issues.Count > 0;

    public bool ShowNoIssues => HasRecords && !HasIssues;

    public bool HasOverLimit => Summary is { OverLimitCount: > 0 };

    public string ScoreText => Summary?.Score?.ToString(CultureInfo.CurrentCulture) ?? "—";

    public ScoreTone Tone => RecordRowViewModel.ToneOf(Summary?.Score);

    public bool IsGood => Tone == ScoreTone.Good;

    public bool IsFair => Tone == ScoreTone.Fair;

    public bool IsPoor => Tone == ScoreTone.Poor;

    public string AnalyzedText => Summary is { } s ? _localizer.Format("Overview_Analyzed", s.AnalyzedCount, s.RecordCount) : string.Empty;

    public string TotalTokensText => (Summary?.TotalTokens ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public string MaxTokensText => (Summary?.MaxTokens ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public string OverLimitText => (Summary?.OverLimitCount ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public string ProgressText => _localizer.Format("Overview_Analyzing", AnalysisDone, AnalysisTotal);

    public double ProgressValue => AnalysisTotal == 0 ? 0 : (double)AnalysisDone / AnalysisTotal;

    /// <summary>
    /// Shows a dataset (or nothing) and starts analysing its pending records.
    /// </summary>
    public async Task ShowAsync(DatasetNodeViewModel? dataset)
    {
        await StopAsync();
        await StopEmbeddingAsync();
        await FlushGuidanceAsync();
        Dataset = dataset;
        Summary = null;
        ApplyJudgments(null);
        ApplySemantic(null);
        SemanticError = null;
        OnPropertyChanged(nameof(CanJudgeRecords));
        OnPropertyChanged(nameof(CanCompareMeaning));
        Histogram.Clear();
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(ShowNoIssues));
        _applyingSettings = true;
        SelectedTokenizer = dataset is null ? null : TokenizerOptions.FirstOrDefault(o => o.Id == dataset.Tokenizer);
        SelectedContextLength = dataset is null ? null : ContextOptions.FirstOrDefault(o => o.Value == dataset.ContextLength)
            ?? new ContextLengthOption(dataset.ContextLength, ContextLabel(dataset.ContextLength));
        AIGuidance = dataset?.AIGuidance;
        OnPropertyChanged(nameof(AIConnectionText));
        OnPropertyChanged(nameof(HasAIConnection));
        _applyingSettings = false;
        if (dataset is null)
        {
            return;
        }

        await RefreshAsync();
        StartAnalysis();
    }

    /// <summary>
    /// Analyses records without up-to-date results (new, edited, restored) in the background.
    /// </summary>
    public void StartAnalysis()
    {
        if (Dataset is not { } dataset)
        {
            return;
        }

        _running?.Cancel();
        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        _runningTask = RunAsync(dataset, cancellation);
    }

    /// <summary>
    /// Cancels a running analysis and waits for it; finished batches are kept.
    /// </summary>
    public async Task StopAsync()
    {
        _running?.Cancel();
        await _runningTask;
    }

    /// <summary>
    /// The current analysis run; awaited by tests.
    /// </summary>
    public Task Running => _runningTask;

    public async Task RefreshAsync()
    {
        if (Dataset is not { } dataset)
        {
            return;
        }

        try
        {
            var summary = await _analysis.GetSummaryAsync(dataset.Id);
            if (Dataset?.Id == dataset.Id)
            {
                Apply(summary);
            }

            await LoadJudgmentsAsync(dataset);
            await LoadSemanticAsync(dataset);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    [RelayCommand]
    private void ShowRecords(IssueSummaryItem? item)
    {
        if (item is not null)
        {
            ShowRecordsRequested?.Invoke(this, item.Filter);
        }
    }

    /// <summary>
    /// Archives the records with an issue (for copies and near-duplicate questions, all but the
    /// first of each group). Undoable.
    /// </summary>
    [RelayCommand]
    private async Task ArchiveAsync(IssueSummaryItem? item)
    {
        if (item is null || Dataset is not { } dataset)
        {
            return;
        }

        try
        {
            var ids = await _analysis.FindAsync(dataset.Id, item.Rule);
            if (ids.Count == 0
                || !await _dialogs.ConfirmAsync(
                    _localizer["Dialog_ArchiveIssuesTitle"],
                    _localizer.Format("Dialog_ArchiveIssuesMessage", ids.Count, item.Label),
                    _localizer["Action_Archive"]))
            {
                return;
            }

            var result = await _operations.SetStatusAsync(dataset.Id, ids, RecordStatus.Archived);
            _status.Set("Status_RecordsArchivedByRule", result.Changed, item.Label);
            await RefreshAsync();
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    [RelayCommand]
    private void OpenAIConnections() => AIConnectionsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ExtractQuestions() => ExtractQuestionsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Saves the AI instructions now instead of after the typing pause (switching datasets, closing).
    /// </summary>
    public async Task FlushGuidanceAsync()
    {
        if (_guidanceDelay is { } delay)
        {
            _guidanceDelay = null;
            await delay.CancelAsync();
            if (Dataset is { } dataset)
            {
                await SaveGuidanceAsync(dataset, AIGuidance);
            }
        }

        await GuidanceSaving;
    }

    partial void OnAIGuidanceChanged(string? value)
    {
        if (_applyingSettings || Dataset is not { } dataset)
        {
            return;
        }

        _guidanceDelay?.Cancel();
        var delay = _guidanceDelay = new CancellationTokenSource();
        GuidanceSaving = SaveLaterAsync();

        async Task SaveLaterAsync()
        {
            try
            {
                await Task.Delay(GuidanceSaveDelay, delay.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_guidanceDelay == delay)
            {
                _guidanceDelay = null;
                await SaveGuidanceAsync(dataset, value);
            }
        }
    }

    private async Task SaveGuidanceAsync(DatasetNodeViewModel dataset, string? guidance)
    {
        guidance = string.IsNullOrWhiteSpace(guidance) ? null : guidance.Trim();
        if (guidance == dataset.AIGuidance)
        {
            return;
        }

        try
        {
            await _catalog.SetAIGuidanceAsync(dataset.Id, guidance);
            dataset.AIGuidance = guidance;
            _status.Set("Status_AIGuidanceSaved", dataset.Name);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    partial void OnSelectedTokenizerChanged(TokenizerOption? value) => _ = SaveSettingsAsync();

    partial void OnSelectedContextLengthChanged(ContextLengthOption? value) => _ = SaveSettingsAsync();

    private async Task SaveSettingsAsync()
    {
        if (_applyingSettings || Dataset is not { } dataset || SelectedTokenizer is not { } tokenizer || SelectedContextLength is not { } context
            || (dataset.Tokenizer == tokenizer.Id && dataset.ContextLength == context.Value))
        {
            return;
        }

        try
        {
            var tokenizerChanged = dataset.Tokenizer != tokenizer.Id;
            await _catalog.SetTokenSettingsAsync(dataset.Id, tokenizer.Id, context.Value);
            dataset.Tokenizer = tokenizer.Id;
            dataset.ContextLength = context.Value;
            _status.Set("Status_TokenSettingsChanged", dataset.Name, tokenizer.Label, context.Label);
            await RefreshAsync();
            // Other tokenizer: every count is stale. Other context length: only the flags change.
            if (tokenizerChanged)
            {
                StartAnalysis();
            }
            else
            {
                AnalysisCompleted?.Invoke(this, dataset.Id);
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    private async Task RunAsync(DatasetNodeViewModel dataset, CancellationTokenSource cancellation)
    {
        try
        {
            var pending = await _analysis.CountPendingAsync(dataset.Id, cancellation.Token);
            if (pending == 0)
            {
                // Nothing to analyse, but statuses may have changed (undo, redo), and an edited
                // record, analysed on its own, may have joined or left a group of near-duplicates.
                var regrouped = await _analysis.GroupNearDuplicatesAsync(dataset.Id, cancellation.Token);
                await RefreshAsync();
                if (regrouped && _running == cancellation)
                {
                    NearGroupsChanged?.Invoke(this, dataset.Id);
                }

                return;
            }

            AnalysisTotal = pending;
            AnalysisDone = 0;
            IsAnalyzing = true;
            var progress = new Progress<int>(done =>
            {
                if (_running == cancellation)
                {
                    AnalysisDone = done;
                }
            });
            await _analysis.AnalyzeAsync(dataset.Id, progress, cancellation.Token);
            if (_running == cancellation)
            {
                await RefreshAsync();
                AnalysisCompleted?.Invoke(this, dataset.Id);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Replaced by a newer run or the dataset was left; finished batches are kept.
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
        finally
        {
            if (_running == cancellation)
            {
                IsAnalyzing = false;
                _running = null;
            }

            cancellation.Dispose();
        }
    }

    private void Apply(DatasetQualitySummary summary)
    {
        Summary = summary;

        Histogram.Clear();
        var largest = Math.Max(1, summary.Histogram.Max(b => b.Count));
        var lower = 0;
        foreach (var bucket in summary.Histogram)
        {
            var label = bucket.UpperBound == int.MaxValue
                ? _localizer.Format("Overview_BucketAbove", lower)
                : _localizer.Format("Overview_BucketBelow", bucket.UpperBound);
            Histogram.Add(new HistogramBar(label, bucket.Count, (double)bucket.Count / largest, Dataset is { } d && lower >= d.ContextLength));
            lower = bucket.UpperBound;
        }

        Issues.Clear();
        foreach (var (rule, count) in summary.IssueCounts.OrderByDescending(p => QualityRules.SeverityOf(p.Key)).ThenByDescending(p => p.Value))
        {
            Issues.Add(new IssueSummaryItem(
                rule,
                QualityRules.SeverityOf(rule),
                IssueText.RuleName(_localizer, rule),
                count,
                FilterFor(rule),
                CanArchive: rule is QualityRule.Duplicate or QualityRule.NearDuplicate or QualityRule.EmptyContent or QualityRule.IdenticalAnswers));
        }

        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(ShowNoIssues));
    }

    private static QualityFilter FilterFor(QualityRule rule) => rule switch
    {
        QualityRule.Duplicate => QualityFilter.Duplicates,
        QualityRule.NearDuplicate => QualityFilter.NearDuplicates,
        QualityRule.PersonalData => QualityFilter.PersonalData,
        QualityRule.OverContextLimit => QualityFilter.OverContextLimit,
        _ when QualityRules.SeverityOf(rule) == IssueSeverity.Error => QualityFilter.Errors,
        _ => QualityFilter.Issues,
    };

    private string TokenizerLabel(string id) => id switch
    {
        Tokenizers.O200kBase => "o200k_base · GPT-4o",
        Tokenizers.Cl100kBase => "cl100k_base · GPT-4 / 3.5",
        _ => id,
    };

    private string ContextLabel(int tokens) => _localizer.Format("Overview_ContextOption", tokens);

    private void Refresh()
    {
        foreach (var option in ContextOptions.Append(SelectedContextLength).OfType<ContextLengthOption>())
        {
            option.Label = ContextLabel(option.Value);
        }

        OnPropertyChanged(nameof(AnalyzedText));
        OnPropertyChanged(nameof(ProgressText));
        if (Summary is { } summary)
        {
            Apply(summary);
        }

        ApplyJudgments(Judgments);
        RefreshSemanticTexts();
        OnPropertyChanged(nameof(JudgedText));
        OnPropertyChanged(nameof(OutdatedText));
        OnPropertyChanged(nameof(JudgeRestText));
    }
}
