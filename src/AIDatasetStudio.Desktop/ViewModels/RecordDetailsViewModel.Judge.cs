using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Judgments;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// Judging with AI (SDD §17.7, M15): the default model rates the shown record from 1 to 5 with a
// reason and compares its answer with records that ask the same thing; the result is kept and
// shown under the quality section until the record is judged again.
public sealed partial class RecordDetailsViewModel
{
    private CancellationTokenSource? _judging;

    /// <summary>
    /// Raised when a record has been judged here, so lists and counts can follow.
    /// </summary>
    public event EventHandler<Guid>? Judged;

    /// <summary>
    /// Raised when the user wants the selected records judged (batch panel).
    /// </summary>
    public event EventHandler? JudgeSelectionRequested;

    /// <summary>
    /// The shown record's judgment and the contradictions found with other records.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJudgment), nameof(IsJudgmentOutdated), nameof(JudgeScoreText), nameof(JudgeReason), nameof(JudgeNotes), nameof(HasJudgeNotes))]
    [NotifyPropertyChangedFor(nameof(JudgeInfo), nameof(JudgeTone), nameof(IsJudgeGood), nameof(IsJudgeFair), nameof(IsJudgePoor), nameof(JudgeActionText))]
    public partial JudgmentDetails Judgment { get; private set; } = JudgmentDetails.None;

    public bool HasJudgment => Judgment.Judgment is not null;

    /// <summary>
    /// The record changed after it was judged (also by a save made here since).
    /// </summary>
    public bool IsJudgmentOutdated => Judgment.Judgment is { } judgment && Record is { } record && judgment.ContentHash != record.ContentHash;

    /// <summary>
    /// "4/5".
    /// </summary>
    public string JudgeScoreText => Judgment.Judgment is { } judgment ? JudgeText.Score(judgment.Score) : string.Empty;

    public string JudgeReason => Judgment.Judgment?.Reason ?? string.Empty;

    /// <summary>
    /// What the judgment found besides the score, and the contradictions with other records.
    /// </summary>
    public IReadOnlyList<string> JudgeNotes => [.. AI.JudgeNotes.Of(_localizer, Judgment)];

    public bool HasJudgeNotes => JudgeNotes.Count > 0;

    /// <summary>
    /// "qwen2.5:7b · 8.10.2026 22:40".
    /// </summary>
    public string JudgeInfo => Judgment.Judgment is { } judgment
        ? _localizer.Format("Judge_Info", judgment.Model, judgment.JudgedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : string.Empty;

    public ScoreTone JudgeTone => JudgeText.ToneOf(Judgment.Judgment?.Score);

    public bool IsJudgeGood => JudgeTone == ScoreTone.Good;

    public bool IsJudgeFair => JudgeTone == ScoreTone.Fair;

    public bool IsJudgePoor => JudgeTone == ScoreTone.Poor;

    public string JudgeActionText => _localizer[HasJudgment ? "Judge_Again" : "Judge_Judge"];

    /// <summary>
    /// Whether records of the dataset can be judged.
    /// </summary>
    public bool CanJudgeRecords => Dataset is { } dataset && Judging.Supports(dataset.Type);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JudgeCommand))]
    public partial bool IsJudging { get; private set; }

    /// <summary>
    /// "Judging with qwen2.5:7b…", then "Comparing with #5…".
    /// </summary>
    [ObservableProperty]
    public partial string? JudgingText { get; private set; }

    /// <summary>
    /// The current or last judging; awaited by tests.
    /// </summary>
    public Task JudgeRun { get; private set; } = Task.CompletedTask;

    private bool CanJudge() => IsJudging || (HasRecord && CanJudgeRecords && !IsWriting);

    /// <summary>
    /// Record › Judge with AI (Mod+J): the model judges the shown record, saved first; while it
    /// judges, the same command stops it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanJudge), AllowConcurrentExecutions = true)]
    private async Task JudgeAsync()
    {
        if (IsJudging)
        {
            _judging?.Cancel();
            await JudgeRun;
            return;
        }

        AIError = null;
        if (_ai.Connection is not { } connection)
        {
            AIError = _localizer["Editor_NoAIConnection"];
            return;
        }

        // The saved record is judged.
        if (!await SaveIfDirtyAsync() || Record is not { } record)
        {
            return;
        }

        if (!Judging.CanJudge(record.Payload))
        {
            AIError = _localizer["Judge_NothingToJudge"];
            return;
        }

        var cancellation = _judging = new CancellationTokenSource();
        IsJudging = true;
        JudgingText = _localizer.Format("Judge_Judging", connection.Model);
        JudgeRun = RunAsync();
        await JudgeRun;

        async Task RunAsync()
        {
            try
            {
                var outcome = await _judge.JudgeAsync(
                    record,
                    Dataset?.AIGuidance,
                    compare: true,
                    connection,
                    ordinal => JudgingText = _localizer.Format("Judge_Comparing", ordinal),
                    cancellation.Token);
                if (Record?.Id == record.Id)
                {
                    Judgment = outcome.Details;
                }

                if (outcome.Verdict is { } verdict)
                {
                    _status.Set("Status_Judged", record.Ordinal, connection.Model, JudgeText.Score(verdict.Score));
                }
                else
                {
                    AIError = _localizer["Judge_Unreadable"];
                }

                Judged?.Invoke(this, record.Id);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _status.Set("Status_JudgingStopped");
                Judged?.Invoke(this, record.Id);
            }
            catch (AIException ex)
            {
                AIError = _ai.Describe(ex, connection);
            }
            catch (Exception ex) when (_status.TryReport(ex))
            {
            }
            finally
            {
                IsJudging = false;
                JudgingText = null;
                _judging = null;
            }
        }
    }

    [RelayCommand]
    private void JudgeSelection() => JudgeSelectionRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Reads the shown record's judgment again, e.g. after records were judged elsewhere.
    /// </summary>
    public Task RefreshJudgmentAsync() => HasRecord ? LoadJudgmentAsync(_generation) : Task.CompletedTask;

    private async Task LoadJudgmentAsync(int generation)
    {
        if (Record is not { } record)
        {
            Judgment = JudgmentDetails.None;
            return;
        }

        try
        {
            var details = await _judge.Judgments.GetAsync(record.Id);
            if (generation == _generation && Record?.Id == record.Id)
            {
                Judgment = details;
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    // Judging the shown record stops when another one is shown; what was judged is kept.
    private async Task StopJudgingAsync()
    {
        _judging?.Cancel();
        await JudgeRun;
    }

    private void RefreshJudgmentTexts()
    {
        OnPropertyChanged(nameof(JudgeNotes));
        OnPropertyChanged(nameof(JudgeInfo));
        OnPropertyChanged(nameof(JudgeActionText));
    }
}
