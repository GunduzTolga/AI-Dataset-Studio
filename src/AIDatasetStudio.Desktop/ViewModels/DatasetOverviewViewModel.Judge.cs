using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Judgments;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// Records a judgment found something in, with a way to list them.
/// </summary>
public sealed record JudgeFindingItem(string Label, long Count, QualityFilter Filter)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

// The AI JUDGMENT card (SDD §17.7, M15): the mean score of the records judged as they are now,
// how the scores spread, what the judgments found, and a way to judge the rest.
public sealed partial class DatasetOverviewViewModel
{
    /// <summary>
    /// Raised when the user wants the records not judged yet to be judged.
    /// </summary>
    public event EventHandler? JudgeRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJudgments), nameof(JudgeAverageText), nameof(JudgedText), nameof(JudgeTone), nameof(IsJudgeGood))]
    [NotifyPropertyChangedFor(nameof(IsJudgeFair), nameof(IsJudgePoor), nameof(OutdatedText), nameof(HasOutdated), nameof(JudgeRestText), nameof(CanJudgeRest))]
    public partial JudgmentSummary? Judgments { get; private set; }

    /// <summary>
    /// Whether records of the dataset can be judged.
    /// </summary>
    public bool CanJudgeRecords => Dataset is { } dataset && Judging.Supports(dataset.Type);

    public bool HasJudgments => Judgments is { HasJudgments: true };

    /// <summary>
    /// "3.8".
    /// </summary>
    public string JudgeAverageText => Judgments?.Average is { } average ? average.ToString("0.0", CultureInfo.CurrentCulture) : "—";

    /// <summary>
    /// "12 of 40 records judged".
    /// </summary>
    public string JudgedText => Judgments is { } judgments ? _localizer.Format("Overview_Judged", judgments.JudgedCount, judgments.RecordCount) : string.Empty;

    public ScoreTone JudgeTone => Judgments?.Average is { } average ? JudgeText.ToneOf((int)Math.Round(average, MidpointRounding.AwayFromZero)) : ScoreTone.None;

    public bool IsJudgeGood => JudgeTone == ScoreTone.Good;

    public bool IsJudgeFair => JudgeTone == ScoreTone.Fair;

    public bool IsJudgePoor => JudgeTone == ScoreTone.Poor;

    /// <summary>
    /// Records per score, 5 first.
    /// </summary>
    public ObservableCollection<HistogramBar> JudgeBars { get; } = [];

    public ObservableCollection<JudgeFindingItem> JudgeFindings { get; } = [];

    public bool HasOutdated => Judgments is { OutdatedCount: > 0 };

    /// <summary>
    /// "3 records changed after they were judged".
    /// </summary>
    public string OutdatedText => _localizer.Format("Overview_JudgeOutdated", Judgments?.OutdatedCount ?? 0);

    public bool CanJudgeRest => Judgments is { NotJudgedCount: > 0 };

    /// <summary>
    /// "Judge 28 records…".
    /// </summary>
    public string JudgeRestText => _localizer.Format("Overview_JudgeRest", Judgments?.NotJudgedCount ?? 0);

    [RelayCommand]
    private void Judge() => JudgeRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowJudged(JudgeFindingItem? item)
    {
        if (item is not null)
        {
            ShowRecordsRequested?.Invoke(this, item.Filter);
        }
    }

    private async Task LoadJudgmentsAsync(DatasetNodeViewModel dataset)
    {
        if (!Judging.Supports(dataset.Type))
        {
            ApplyJudgments(null);
            return;
        }

        var judgments = await _judgments.GetSummaryAsync(dataset.Id);
        if (Dataset?.Id == dataset.Id)
        {
            ApplyJudgments(judgments);
        }
    }

    private void ApplyJudgments(JudgmentSummary? judgments)
    {
        Judgments = judgments;
        JudgeBars.Clear();
        JudgeFindings.Clear();
        if (judgments is not { HasJudgments: true })
        {
            return;
        }

        var largest = Math.Max(1, judgments.ScoreCounts.Max());
        for (var score = Judging.MaxScore; score >= Judging.MinScore; score--)
        {
            var count = judgments.ScoreCounts[score - 1];
            JudgeBars.Add(new HistogramBar(JudgeText.Score(score), count, (double)count / largest, score <= Judging.LowScore));
        }

        void Add(string key, long count, QualityFilter filter)
        {
            if (count > 0)
            {
                JudgeFindings.Add(new JudgeFindingItem(_localizer[key], count, filter));
            }
        }

        Add("QualityFilter_LowJudgeScore", judgments.LowCount, QualityFilter.LowJudgeScore);
        Add("QualityFilter_Ungrounded", judgments.UngroundedCount, QualityFilter.Ungrounded);
        Add("QualityFilter_PreferenceDoubts", judgments.PreferenceCount, QualityFilter.PreferenceDoubts);
        Add("QualityFilter_Contradictions", judgments.ContradictionCount, QualityFilter.Contradictions);
    }
}
