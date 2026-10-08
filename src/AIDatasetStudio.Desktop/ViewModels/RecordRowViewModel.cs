using System.Globalization;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// How a quality score is shown: good (90–100), fair (60–89), poor (below 60).
/// </summary>
public enum ScoreTone
{
    None,
    Good,
    Fair,
    Poor,
}

public sealed partial class RecordRowViewModel : ViewModelBase
{
    /// <summary>
    /// Tags shown in the row; the rest are counted ("+2").
    /// </summary>
    public const int MaxVisibleTags = 2;

    private readonly Localizer _localizer;

    public RecordRowViewModel(RecordSummary summary, Localizer localizer)
    {
        _localizer = localizer;
        Id = summary.Id;
        Ordinal = summary.Ordinal;
        Preview = summary.Preview;
        Status = summary.Status;
        Apply(summary);
    }

    public Guid Id { get; }

    public long Ordinal { get; }

    public string OrdinalText => $"#{Ordinal}";

    [ObservableProperty]
    public partial string Preview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    public partial RecordStatus Status { get; set; }

    /// <summary>
    /// Content tokens; <c>null</c> until the record is analysed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenText))]
    public partial int? TotalTokens { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText), nameof(Tone), nameof(IsGood), nameof(IsFair), nameof(IsPoor), nameof(IsAnalyzed))]
    public partial int? Score { get; private set; }

    [ObservableProperty]
    public partial bool IsOverContextLimit { get; private set; }

    /// <summary>
    /// A model's score 1–5 of the record as it is now; <c>null</c> when not judged so (SDD §17.7).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJudged), nameof(JudgeScoreText), nameof(JudgeTip), nameof(IsJudgeGood), nameof(IsJudgeFair), nameof(IsJudgePoor))]
    public partial int? JudgeScore { get; private set; }

    public bool IsJudged => JudgeScore is not null;

    public string JudgeScoreText => JudgeScore?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>
    /// "Score from the model: 4/5".
    /// </summary>
    public string? JudgeTip => JudgeScore is { } score ? _localizer.Format("Judge_ScoreTip", JudgeText.Score(score)) : null;

    public bool IsJudgeGood => JudgeText.ToneOf(JudgeScore) == ScoreTone.Good;

    public bool IsJudgeFair => JudgeText.ToneOf(JudgeScore) == ScoreTone.Fair;

    public bool IsJudgePoor => JudgeText.ToneOf(JudgeScore) == ScoreTone.Poor;

    /// <summary>
    /// The record's tags, kept in step with tag changes made elsewhere.
    /// </summary>
    public IReadOnlyList<Guid> TagIds { get; private set; } = [];

    /// <summary>
    /// The tags (colored dot and name), by name.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags), nameof(VisibleTags), nameof(MoreTagsText), nameof(HasMoreTags), nameof(TagsTip))]
    public partial IReadOnlyList<Tags.TagViewModel> Tags { get; private set; } = [];

    public bool HasTags => Tags.Count > 0;

    /// <summary>
    /// The first <see cref="MaxVisibleTags"/> tags, shown next to the content.
    /// </summary>
    public IReadOnlyList<Tags.TagViewModel> VisibleTags => Tags.Count <= MaxVisibleTags ? Tags : [.. Tags.Take(MaxVisibleTags)];

    /// <summary>
    /// "+2" for the tags that do not fit.
    /// </summary>
    public string MoreTagsText => HasMoreTags ? "+" + (Tags.Count - MaxVisibleTags).ToString(CultureInfo.CurrentCulture) : string.Empty;

    public bool HasMoreTags => Tags.Count > MaxVisibleTags;

    /// <summary>
    /// All tag names, for the tooltip.
    /// </summary>
    public string? TagsTip => HasTags ? string.Join(", ", Tags.Select(t => t.Name)) : null;

    public string StatusLabel => Labels.For(_localizer, Status);

    public string TokenText => TotalTokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";

    public string ScoreText => Score?.ToString(CultureInfo.CurrentCulture) ?? "—";

    public bool IsAnalyzed => Score is not null;

    public ScoreTone Tone => ToneOf(Score);

    public bool IsGood => Tone == ScoreTone.Good;

    public bool IsFair => Tone == ScoreTone.Fair;

    public bool IsPoor => Tone == ScoreTone.Poor;

    public RecordCursor Cursor => new(Ordinal, Id);

    public static ScoreTone ToneOf(int? score) => score switch
    {
        null => ScoreTone.None,
        >= 90 => ScoreTone.Good,
        >= 60 => ScoreTone.Fair,
        _ => ScoreTone.Poor,
    };

    /// <summary>
    /// Takes tokens and score from a fresh list row (after an analysis).
    /// </summary>
    public void Apply(RecordSummary summary)
    {
        TotalTokens = summary.TotalTokens;
        Score = summary.Score;
        IsOverContextLimit = summary.IsOverContextLimit;
        JudgeScore = summary.JudgeScore;
    }

    /// <summary>
    /// Shows these tags; <paramref name="catalog"/> gives their names and colors.
    /// </summary>
    public void SetTags(IReadOnlyList<Guid> tagIds, Tags.TagCatalog catalog)
    {
        TagIds = tagIds;
        Tags = catalog.Resolve(tagIds);
    }

    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(TokenText));
        OnPropertyChanged(nameof(JudgeTip));
    }
}
