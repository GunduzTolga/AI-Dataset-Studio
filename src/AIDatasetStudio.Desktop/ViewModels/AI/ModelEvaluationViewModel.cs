using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.AI;

/// <summary>
/// The dataset a model is evaluated on.
/// </summary>
/// <param name="LastExport">The options of the dataset's last export, whose split tells the questions a trained model has not seen.</param>
public sealed record EvaluationTarget(Guid DatasetId, string Name, DatasetType Type, ExportOptions? LastExport = null);

/// <summary>
/// An earlier evaluation of the dataset, named in the current language.
/// </summary>
public sealed class EvaluationRunRow(EvaluationRun run, Localizer localizer) : ViewModelBase
{
    public EvaluationRun Run { get; } = run;

    public string Model => Run.Model;

    public string DateText => Run.RanAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// "graded by qwen2.5:32b · test part, seed 42 · 20 questions · 9.10.2026 03:12".
    /// </summary>
    public string DetailText => localizer.Format("Evaluate_RunDetail", Run.Judge, SourceText, Run.Summary.Answered, DateText);

    public string ScoreText => Run.Summary.AverageScore is { } score ? score.ToString("0.0", CultureInfo.CurrentCulture) + " / 5" : "–";

    public bool IsGood => Run.Summary.AverageScore >= 4;

    public bool IsLow => Run.Summary.AverageScore < 3;

    public string F1Text => "F1 " + (Run.Summary.AverageF1 ?? 0).ToString("0.00", CultureInfo.CurrentCulture);

    public string DistributionText => ModelEvaluationViewModel.Distribution(Run.Summary);

    private string SourceText => Run.Source switch
    {
        EvaluationSource.TestPart => localizer.Format("Evaluate_RunTest", Run.Seed),
        EvaluationSource.ValidationPart => localizer.Format("Evaluate_RunValidation", Run.Seed),
        _ => localizer.Format("Evaluate_RunAll", Run.Seed),
    };

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// A choice of where the questions come from, named in the current language.
/// </summary>
public sealed class EvaluationSourceOption(EvaluationSource source, Func<string> label) : ViewModelBase
{
    public EvaluationSource Source { get; } = source;

    public string Label => label();

    internal void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// A connection to choose as the model or the grader.
/// </summary>
public sealed record EvaluationModelOption(AIConnection Connection, string Label);

/// <summary>
/// One question of an evaluation, filled in as the model answers and the grader scores.
/// </summary>
public sealed partial class EvaluationRow : ViewModelBase
{
    internal EvaluationRow(long ordinal, EvaluationItem item)
    {
        Ordinal = ordinal;
        Item = item;
    }

    public long Ordinal { get; }

    internal EvaluationItem Item { get; }

    public string Question => Item.Question;

    public string Reference => Item.Reference;

    [ObservableProperty]
    public partial string Answer { get; internal set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText), nameof(IsLow), nameof(IsGood))]
    public partial EvaluationResult? Result { get; internal set; }

    [ObservableProperty]
    public partial string State { get; internal set; } = string.Empty;

    public string ScoreText => Result?.Score is { } score ? $"{score}/5" : "–";

    public bool IsLow => Result?.Score is <= Judging.LowScore;

    public bool IsGood => Result?.Score is >= 4;
}

/// <summary>
/// Tools › Evaluate a Model (M22, SDD §21 V4): a model answers a sample of the dataset's
/// questions, and a grader model compares each answer with the dataset's own answer. The
/// questions are asked as a model trained on the dataset would see them, at temperature 0.
/// The report can be saved as JSON Lines; nothing is written to the workspace.
/// </summary>
public sealed partial class ModelEvaluationViewModel : ViewModelBase, IDisposable
{
    private readonly AIAssistant _ai;
    private readonly IRecordQueryService _queries;
    private readonly IExportService _export;
    private readonly IEvaluationRunService _runs;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private CancellationTokenSource? _work;

    /// <param name="history">The dataset's earlier evaluations, the latest first.</param>
    internal ModelEvaluationViewModel(ModelEvaluator services, EvaluationTarget target, IReadOnlyList<EvaluationRun> history)
    {
        _ai = services.AI;
        _queries = services.Queries;
        _export = services.Export;
        _runs = services.Runs;
        _files = services.Files;
        _dialogs = services.Dialogs;
        _localizer = services.Localizer;
        Target = target;
        History = [.. history.Select(run => new EvaluationRunRow(run, _localizer))];
        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HistoryText));
        };
        Models = [.. _ai.Connections.Select(c => new EvaluationModelOption(c, AIText.Describe(c)))];
        SelectedModel = Models.FirstOrDefault(m => m.Connection.Id == _ai.Connection?.Id) ?? Models.FirstOrDefault();
        SelectedJudge = SelectedModel;

        // Questions the model was not trained on, when an export split the dataset.
        var split = target.LastExport?.Split;
        if (split is { TestPercent: > 0 })
        {
            Sources.Add(new EvaluationSourceOption(EvaluationSource.TestPart, () => _localizer.Format("Evaluate_SourceTest", split.TestPercent, split.Seed)));
        }

        if (split is { ValidationPercent: > 0 })
        {
            Sources.Add(new EvaluationSourceOption(EvaluationSource.ValidationPart, () => _localizer.Format("Evaluate_SourceValidation", split.ValidationPercent, split.Seed)));
        }

        Sources.Add(new EvaluationSourceOption(EvaluationSource.AllRecords, () => _localizer["Evaluate_SourceAll"]));
        SelectedSource = Sources[0];
        _localizer.CultureChanged += OnCultureChanged;
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    public EvaluationTarget Target { get; }

    public string TargetText => _localizer.Format("Export_Target", Target.Name, Labels.For(_localizer, Target.Type));

    public IReadOnlyList<EvaluationModelOption> Models { get; }

    public bool HasModels => Models.Count > 0;

    /// <summary>
    /// The model whose answers are evaluated.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial EvaluationModelOption? SelectedModel { get; set; }

    /// <summary>
    /// The model that grades them; the default connection unless chosen otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial EvaluationModelOption? SelectedJudge { get; set; }

    public ObservableCollection<EvaluationSourceOption> Sources { get; } = [];

    /// <summary>
    /// Where the questions come from: the test part of the last export unless there is none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllRecords), nameof(SourceHint))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial EvaluationSourceOption? SelectedSource { get; set; }

    public bool IsAllRecords => SelectedSource?.Source == EvaluationSource.AllRecords;

    public string SourceHint => SelectedSource?.Source switch
    {
        EvaluationSource.TestPart => _localizer["Evaluate_SourceTestHint"],
        EvaluationSource.ValidationPart => _localizer["Evaluate_SourceValidationHint"],
        _ => _localizer["Evaluate_SourceAllHint"],
    };

    [ObservableProperty]
    public partial decimal? SampleSize { get; set; } = ModelEvaluation.DefaultSampleSize;

    [ObservableProperty]
    public partial decimal? Seed { get; set; } = 42;

    /// <summary>
    /// Also ask records not approved yet (archived records never are), when asking all records;
    /// a part of the split has the statuses its export chose.
    /// </summary>
    [ObservableProperty]
    public partial bool IncludeUnapproved { get; set; }

    public ObservableCollection<EvaluationRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial EvaluationRow? SelectedRow { get; set; }

    public bool HasSelection => SelectedRow is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ExportCommand))]
    public partial bool IsRunning { get; private set; }

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// "Average 3.8 / 5 · word overlap 0.62 · 19 of 20 scored".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial EvaluationSummary? Summary { get; private set; }

    public bool HasSummary => Summary is { Answered: > 0 };

    public string SummaryText => Summary is { Answered: > 0 } s
        ? _localizer.Format(
            "Evaluate_Summary",
            s.AverageScore is { } score ? score.ToString("0.0", CultureInfo.CurrentCulture) : "–",
            (s.AverageF1 ?? 0).ToString("0.00", CultureInfo.CurrentCulture),
            s.Scored,
            s.Answered)
        : string.Empty;

    public string DistributionText => Summary is { Answered: > 0 } s ? Distribution(s) : string.Empty;

    /// <summary>
    /// The dataset's earlier evaluations, the latest first; a finished evaluation joins them.
    /// </summary>
    public ObservableCollection<EvaluationRunRow> History { get; }

    public bool HasHistory => History.Count > 0;

    /// <summary>
    /// "Earlier evaluations (3)".
    /// </summary>
    public string HistoryText => _localizer.Format("Evaluate_History", History.Count);

    /// <summary>
    /// Shows the earlier evaluations instead of the questions.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowHistory { get; set; }

    /// <summary>
    /// The current run; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    private bool CanRun() => !IsRunning && SelectedModel is not null && SelectedJudge is not null && SelectedSource is not null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync()
    {
        PendingWork = RunCoreAsync();
        return PendingWork;
    }

    [RelayCommand]
    private void Stop() => _work?.Cancel();

    private bool CanExport() => !IsRunning && Summary is { Answered: > 0 };

    /// <summary>
    /// Saves every answer with its reference, score, reason and word overlap as JSON Lines.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var model = SelectedModel?.Connection.Model ?? string.Empty;
        if (await _files.PickExportFileAsync(Export.ExportWizardViewModel.SuggestedFileName($"{Target.Name} - {model}")) is not { } path)
        {
            return;
        }

        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var lines = Rows.Where(r => r.Result is not null).Select(r => JsonSerializer.Serialize(new
        {
            record = r.Ordinal,
            model,
            judge = SelectedJudge?.Connection.Model,
            question = r.Question,
            reference = r.Reference,
            answer = r.Result!.Answer,
            score = r.Result.Score,
            reason = r.Result.Reason,
            f1 = Math.Round(r.Result.F1, 4),
        }, options));
        try
        {
            await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ErrorMessage = null;
            ProgressText = _localizer.Format("Evaluate_Saved", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = _localizer.Format("Evaluate_SaveFailed", ex.Message);
        }
    }

    /// <summary>
    /// Removes an earlier evaluation from the dataset, after asking.
    /// </summary>
    [RelayCommand]
    private async Task DeleteRunAsync(EvaluationRunRow? row)
    {
        if (row is null || !await _dialogs.ConfirmAsync(
                _localizer["Evaluate_DeleteRunTitle"],
                _localizer.Format("Evaluate_DeleteRunMessage", row.Model, row.DateText),
                _localizer["Action_Delete"],
                isDestructive: true))
        {
            return;
        }

        try
        {
            await _runs.DeleteAsync(row.Run.Id);
            History.Remove(row);
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            ErrorMessage = _localizer.Format(error.Key, error.Args);
        }
    }

    internal static string Distribution(EvaluationSummary summary) =>
        string.Join(" · ", Enumerable.Range(1, Judging.MaxScore).Reverse().Select(score => $"{score}/5: {summary.Counts[score - 1]}"));

    [RelayCommand]
    private void Close()
    {
        _work?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops the models and lets go of the application's events once the window is closed.
    /// </summary>
    public void Dispose()
    {
        _work?.Cancel();
        _localizer.CultureChanged -= OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SourceHint));
        OnPropertyChanged(nameof(HistoryText));
        foreach (var source in Sources)
        {
            source.Refresh();
        }

        foreach (var run in History)
        {
            run.Refresh();
        }
    }

    private async Task RunCoreAsync()
    {
        if (SelectedModel?.Connection is not { } model || SelectedJudge?.Connection is not { } judge)
        {
            return;
        }

        using var work = new CancellationTokenSource();
        _work = work;
        var token = work.Token;
        var source = SelectedSource?.Source ?? EvaluationSource.AllRecords;
        var seed = (int)(Seed ?? 42);
        IsRunning = true;
        ShowHistory = false;
        ErrorMessage = null;
        Rows.Clear();
        Summary = null;
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DistributionText));
        var connection = model;
        try
        {
            ProgressText = _localizer["Evaluate_Sampling"];
            var sample = ModelEvaluation.Sample(await AllIdsAsync(source, token), (int)Math.Clamp(SampleSize ?? ModelEvaluation.DefaultSampleSize, 1, ModelEvaluation.MaxSampleSize), seed);
            var language = (_localizer.Culture.IsNeutralCulture ? _localizer.Culture : _localizer.Culture.Parent).EnglishName;
            var results = new List<EvaluationResult>();
            for (var i = 0; i < sample.Count; i++)
            {
                ProgressValue = (double)i / sample.Count;
                ProgressText = _localizer.Format("Evaluate_Progress", i + 1, sample.Count, model.Model);
                if (await _queries.GetAsync(sample[i], token) is not { } record || ModelEvaluation.Item(record.Payload) is not { } item)
                {
                    continue;
                }

                var row = new EvaluationRow(record.Ordinal, item) { State = _localizer["Evaluate_Answering"] };
                Rows.Add(row);
                SelectedRow ??= row;
                connection = model;
                var answer = new StringBuilder();
                await foreach (var piece in _ai.WriteAsync(model, item.Messages, token, ModelEvaluation.Temperature))
                {
                    answer.Append(piece);
                    row.Answer = answer.ToString();
                }

                row.State = _localizer["Evaluate_Grading"];
                connection = judge;
                var verdict = new StringBuilder();
                await foreach (var piece in _ai.WriteAsync(judge, ModelEvaluation.GradePrompt(item, answer.ToString(), language), token, Judging.Temperature))
                {
                    verdict.Append(piece);
                }

                row.Result = ModelEvaluation.Result(item, answer.ToString(), verdict.ToString());
                row.State = row.Result.Score is null ? _localizer["Evaluate_Unreadable"] : row.Result.Reason;
                results.Add(row.Result);
                Summary = ModelEvaluation.Summarize(results);
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(DistributionText));
            }

            ProgressValue = 1;
            ProgressText = Rows.Count == 0 ? _localizer["Evaluate_NothingToAsk"] : _localizer.Format("Evaluate_Done", Rows.Count);
            if (results.Count > 0)
            {
                // Finished: kept with the dataset to compare with other models. A stopped run is not.
                var run = await _runs.AddAsync(Target.DatasetId, model.Model, judge.Model, source, seed, ModelEvaluation.Summarize(results), CancellationToken.None);
                History.Insert(0, new EvaluationRunRow(run, _localizer));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ProgressText = _localizer.Format("Evaluate_Stopped", Summary?.Answered ?? 0);
        }
        catch (AIException ex)
        {
            ErrorMessage = _ai.Describe(ex, connection);
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            ErrorMessage = _localizer.Format(error.Key, error.Args);
        }
        finally
        {
            _work = null;
            IsRunning = false;
        }
    }

    // The records that can be asked: a part of the last export's split, or approved records (or
    // every record not archived).
    private async Task<IReadOnlyList<Guid>> AllIdsAsync(EvaluationSource source, CancellationToken cancellationToken)
    {
        if (source is EvaluationSource.TestPart or EvaluationSource.ValidationPart && Target.LastExport is { } last)
        {
            var part = source == EvaluationSource.TestPart ? SplitPart.Test : SplitPart.Validation;
            return await _export.GetSplitPartAsync(Target.DatasetId, last, part, cancellationToken);
        }

        IReadOnlySet<RecordStatus> statuses = IncludeUnapproved
            ? new HashSet<RecordStatus> { RecordStatus.Approved, RecordStatus.InReview, RecordStatus.Draft }
            : new HashSet<RecordStatus> { RecordStatus.Approved };
        var ids = new List<Guid>();
        RecordCursor? after = null;
        do
        {
            var page = await _queries.GetPageAsync(Target.DatasetId, new RecordFilter(statuses), after, 500, cancellationToken);
            ids.AddRange(page.Items.Select(item => item.Id));
            after = page.Next;
        }
        while (after is not null);
        return ids;
    }
}
