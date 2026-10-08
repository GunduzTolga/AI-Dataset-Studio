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
    }

    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(TokenText));
    }
}
