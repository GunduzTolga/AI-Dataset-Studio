using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.ViewModels.AI;

/// <summary>
/// A record in the judge window: whether to judge it, and what the model said.
/// </summary>
public sealed partial class JudgeRecordViewModel : ViewModelBase
{
    private const int TitleLength = 90;

    private readonly Localizer _localizer;
    private readonly Action _changed;

    public JudgeRecordViewModel(RecordSummary summary, Localizer localizer, Action changed)
    {
        Summary = summary;
        _localizer = localizer;
        _changed = changed;
        // Judged as it is now: nothing to do unless asked to judge it again.
        State = summary.JudgeScore is null ? WritingState.Waiting : WritingState.Done;
    }

    public RecordSummary Summary { get; }

    public Guid Id => Summary.Id;

    /// <summary>
    /// "#12".
    /// </summary>
    public string Number => "#" + Summary.Ordinal.ToString(CultureInfo.InvariantCulture);

    public string Title => Summary.Preview.Length <= TitleLength ? Summary.Preview : Summary.Preview[..TitleLength].TrimEnd() + "…";

    /// <summary>
    /// The record as saved; read when it is shown or judged.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Question), nameof(Answer), nameof(HasAnswer))]
    public partial RecordDetails? Details { get; set; }

    public string Question => Details is { } details ? Variations.SourceOf(details.Payload)?.Question ?? string.Empty : Summary.Preview;

    public string Answer => Details is { } details ? Variations.SourceOf(details.Payload)?.Answer ?? string.Empty : string.Empty;

    public bool HasAnswer => Answer.Length > 0;

    /// <summary>
    /// The record's judgment and contradictions as kept.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJudgment), nameof(Score), nameof(ScoreText), nameof(Reason), nameof(Notes), nameof(HasNotes))]
    [NotifyPropertyChangedFor(nameof(IsOutdated), nameof(Info), nameof(IsGood), nameof(IsFair), nameof(IsPoor), nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsScored), nameof(ShowsStateTag), nameof(ShowsNoJudgment))]
    public partial JudgmentDetails Judgment { get; set; } = JudgmentDetails.None;

    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsWriting), nameof(HasProblem), nameof(IsScored), nameof(ShowsStateTag), nameof(ShowsNoJudgment))]
    public partial WritingState State { get; set; }

    /// <summary>
    /// "Judging with qwen2.5:7b…", then "Comparing with #5…".
    /// </summary>
    [ObservableProperty]
    public partial string? Activity { get; set; }

    /// <summary>
    /// What the model answered, shown when it had no score.
    /// </summary>
    [ObservableProperty]
    public partial string? RawAnswer { get; set; }

    public bool IsWriting => State == WritingState.Writing;

    public bool HasProblem => State == WritingState.Unreadable;

    public bool HasJudgment => Judgment.Judgment is not null;

    /// <summary>
    /// The current score, or the score of the list row until the judgment is read.
    /// </summary>
    public int? Score => Judgment.Judgment is { } judgment ? (Judgment.IsCurrent ? judgment.Score : null) : Summary.JudgeScore;

    public string ScoreText => Judgment.Judgment is { } judgment ? JudgeText.Score(judgment.Score) : string.Empty;

    public bool IsGood => JudgeText.ToneOf(Score ?? Judgment.Judgment?.Score) == ScoreTone.Good;

    public bool IsFair => JudgeText.ToneOf(Score ?? Judgment.Judgment?.Score) == ScoreTone.Fair;

    public bool IsPoor => JudgeText.ToneOf(Score ?? Judgment.Judgment?.Score) == ScoreTone.Poor;

    /// <summary>
    /// Judged as it is now: the list shows the score.
    /// </summary>
    public bool IsScored => State == WritingState.Done && Score is not null;

    /// <summary>
    /// The list shows the state instead: judging, nothing to judge, unreadable.
    /// </summary>
    public bool ShowsStateTag => !IsScored && StateText.Length > 0;

    public bool ShowsNoJudgment => !HasJudgment && !IsWriting && !HasProblem;

    public string Reason => Judgment.Judgment?.Reason ?? string.Empty;

    public bool IsOutdated => HasJudgment && !Judgment.IsCurrent;

    public string Info => Judgment.Judgment is { } judgment
        ? _localizer.Format("Judge_Info", judgment.Model, judgment.JudgedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>
    /// What the judgment found besides the score, and the contradictions with other records.
    /// </summary>
    public IReadOnlyList<string> Notes => [.. JudgeNotes.Of(_localizer, Judgment)];

    public bool HasNotes => Notes.Count > 0;

    /// <summary>
    /// The state in the list: "Judging…", "4/5", "Nothing to judge", "Unreadable".
    /// </summary>
    public string StateText => State switch
    {
        WritingState.Writing => _localizer["Judge_StateJudging"],
        WritingState.Done when Judgment.Judgment is { } judgment => JudgeText.Score(judgment.Score),
        WritingState.Done when Summary.JudgeScore is { } score => JudgeText.Score(score),
        WritingState.Empty => _localizer["Judge_StateSkipped"],
        WritingState.Unreadable => _localizer["QA_StateUnreadable"],
        _ => string.Empty,
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Info));
    }

    partial void OnIsIncludedChanged(bool value) => _changed();
}

/// <summary>
/// The lines under a judgment: what it found besides the score, and contradictions.
/// </summary>
public static class JudgeNotes
{
    public static IEnumerable<string> Of(Localizer localizer, JudgmentDetails details)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(details);
        if (details.Judgment is { } judgment)
        {
            if (judgment.Flags.HasFlag(JudgeFlags.Ungrounded))
            {
                yield return localizer["Judge_Ungrounded"];
            }

            if (judgment.Flags.HasFlag(JudgeFlags.RejectedBetter))
            {
                yield return localizer["Judge_RejectedBetter"];
            }

            if (judgment.Flags.HasFlag(JudgeFlags.NoPreference))
            {
                yield return localizer["Judge_NoPreference"];
            }
        }

        foreach (var contradiction in details.Contradictions)
        {
            yield return localizer.Format("Judge_Contradicts", contradiction.OtherOrdinal, contradiction.Reason);
        }
    }
}
