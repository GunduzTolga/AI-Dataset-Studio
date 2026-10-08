using System.Globalization;
using AIDatasetStudio.Application.AI;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// How a model's score 1–5 is shown (SDD §17.7).
/// </summary>
public static class JudgeText
{
    /// <summary>
    /// 4–5 good, 3 fair, 1–2 poor.
    /// </summary>
    public static ScoreTone ToneOf(int? score) => score switch
    {
        null => ScoreTone.None,
        > Judging.LowScore + 1 => ScoreTone.Good,
        > Judging.LowScore => ScoreTone.Fair,
        _ => ScoreTone.Poor,
    };

    /// <summary>
    /// "4/5".
    /// </summary>
    public static string Score(int score) => string.Create(CultureInfo.CurrentCulture, $"{score}/{Judging.MaxScore}");
}
