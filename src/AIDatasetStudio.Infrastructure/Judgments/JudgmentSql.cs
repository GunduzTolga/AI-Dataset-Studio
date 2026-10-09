using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Infrastructure.Analysis;

namespace AIDatasetStudio.Infrastructure.Judgments;

/// <summary>
/// SQL fragments for judgments (SDD §17.7). Alias <c>r</c> = DatasetRecords. A judgment counts
/// while the record is as it was judged; a contradiction while both records are as they were
/// compared and the other one is not archived.
/// </summary>
internal static class JudgmentSql
{
    private const string Current = "FROM RecordJudgments j WHERE j.RecordId = r.Id AND j.ContentHash = r.ContentHash";

    /// <summary>
    /// The current score of r, or NULL.
    /// </summary>
    public const string Score = $"(SELECT j.Score {Current})";

    /// <summary>
    /// 1 when r contradicts another record that is not archived, as both are now.
    /// </summary>
    public static readonly string HasContradiction = string.Create(
        CultureInfo.InvariantCulture,
        $"(EXISTS (SELECT 1 FROM RecordConflicts c JOIN DatasetRecords o ON o.Id = c.OtherId WHERE c.RecordId = r.Id " +
        $"AND c.Contradicts = 1 AND c.RecordHash = r.ContentHash AND c.OtherHash = o.ContentHash AND o.Status <> {AnalysisSql.Archived}) " +
        $"OR EXISTS (SELECT 1 FROM RecordConflicts c JOIN DatasetRecords o ON o.Id = c.RecordId WHERE c.OtherId = r.Id " +
        $"AND c.Contradicts = 1 AND c.OtherHash = r.ContentHash AND c.RecordHash = o.ContentHash AND o.Status <> {AnalysisSql.Archived}))");

    /// <summary>
    /// The WHERE condition for a judgment filter, or <c>null</c> for another filter.
    /// </summary>
    public static string? Condition(QualityFilter filter) => filter switch
    {
        QualityFilter.LowJudgeScore => $"EXISTS (SELECT 1 {Current} AND j.Score <= {Judging.LowScore})",
        QualityFilter.NotJudged => $"NOT EXISTS (SELECT 1 {Current})",
        QualityFilter.Ungrounded => $"EXISTS (SELECT 1 {Current} AND (j.Flags & {(int)JudgeFlags.Ungrounded}) <> 0)",
        QualityFilter.PreferenceDoubts => $"EXISTS (SELECT 1 {Current} AND (j.Flags & {(int)(JudgeFlags.RejectedBetter | JudgeFlags.NoPreference)}) <> 0)",
        QualityFilter.Contradictions => HasContradiction,
        _ => null,
    };
}
