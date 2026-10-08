using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Infrastructure.Analysis;

/// <summary>
/// SQL fragments shared by the analysis and record queries. Aliases: <c>r</c> = DatasetRecords,
/// <c>d</c> = Datasets, <c>a</c> = RecordAnalyses.
/// </summary>
internal static class AnalysisSql
{
    /// <summary>
    /// Joins the analysis only while it is up to date: same content, the dataset's current
    /// tokenizer and the current rules. Anything else counts as "not analysed".
    /// </summary>
    public static readonly string FreshJoin =
        "LEFT JOIN RecordAnalyses a ON a.RecordId = r.Id AND a.ContentHash = r.ContentHash " +
        $"AND a.Tokenizer = d.DefaultTokenizer AND a.RulesVersion = {QualityRules.Version}";

    // Cross-record rules only compare with records that are not archived: archived records are out
    // of the dataset (and of exports), so archiving one copy or one of two answers settles the issue.

    /// <summary>
    /// 1 when another record of the dataset, not archived, has the same content.
    /// </summary>
    public static readonly string IsDuplicate =
        "EXISTS (SELECT 1 FROM DatasetRecords o WHERE o.DatasetId = r.DatasetId AND o.ContentHash = r.ContentHash AND o.Id <> r.Id " +
        $"AND o.Status <> {Archived})";

    /// <summary>
    /// 1 when an up-to-date analysis of another record, not archived, asks the same question with different content.
    /// </summary>
    public static readonly string IsConflicting =
        "(a.QuestionKey IS NOT NULL AND EXISTS (SELECT 1 FROM RecordAnalyses a2 JOIN DatasetRecords r2 ON r2.Id = a2.RecordId " +
        "AND a2.ContentHash = r2.ContentHash WHERE a2.DatasetId = r.DatasetId AND a2.QuestionKey = a.QuestionKey " +
        $"AND a2.Tokenizer = a.Tokenizer AND a2.RulesVersion = {QualityRules.Version} AND r2.ContentHash <> r.ContentHash " +
        $"AND r2.Status <> {Archived}))";

    // Another record with an up-to-date analysis, not archived, in the same group of near-duplicate
    // questions but with a different question (exactly the same question is another rule's business)
    // and not of the same family: variations are meant to ask the same thing (SDD §17.6).
    private static readonly string NearOthers =
        "FROM RecordAnalyses a3 JOIN DatasetRecords r3 ON r3.Id = a3.RecordId AND a3.ContentHash = r3.ContentHash " +
        "WHERE a3.DatasetId = r.DatasetId AND a3.NearGroup = a.NearGroup AND a3.QuestionKey IS NOT a.QuestionKey " +
        $"AND a3.Tokenizer = a.Tokenizer AND a3.RulesVersion = {QualityRules.Version} AND r3.Status <> {Archived} " +
        $"AND {Family("r3")} IS NOT {Family("r")}";

    /// <summary>
    /// 1 when another record, not archived, asks a near-duplicate question (<see cref="NearDuplicates"/>).
    /// </summary>
    public static readonly string IsNearDuplicate = $"(a.NearGroup IS NOT NULL AND EXISTS (SELECT 1 {NearOthers}))";

    /// <summary>
    /// The number of the first such record, or NULL.
    /// </summary>
    public static readonly string NearDuplicateOf = $"(CASE WHEN a.NearGroup IS NULL THEN NULL ELSE (SELECT MIN(r3.Ordinal) {NearOthers}) END)";

    public const string IsOverLimit = "(a.TotalTokens > d.ContextLength)";

    /// <summary>
    /// The record a variation was written from, as its metadata has it (<c>alias</c> has a
    /// MetadataJson column), or NULL.
    /// </summary>
    public static string VariantOf(string alias) =>
        $"(CASE WHEN json_valid({alias}.MetadataJson) THEN json_extract({alias}.MetadataJson, '{Application.Records.VariantOf.IdPath}') END)";

    /// <summary>
    /// The family of a record (<c>alias</c> of DatasetRecords): the record it is a variation of,
    /// otherwise itself, written as stored ids are.
    /// </summary>
    public static string Family(string alias) => $"COALESCE(upper({VariantOf(alias)}), {alias}.Id)";

    /// <summary>
    /// The score with cross-record and context penalties; NULL when not analysed (SDD §8.1).
    /// </summary>
    public static readonly string Score =
        $"CASE WHEN a.RecordId IS NULL THEN NULL ELSE MAX(0, a.Score " +
        $"- CASE WHEN {IsDuplicate} THEN {QualityRules.WarningPenalty} ELSE 0 END " +
        $"- CASE WHEN {IsConflicting} THEN {QualityRules.WarningPenalty} ELSE 0 END " +
        $"- CASE WHEN {IsNearDuplicate} THEN {QualityRules.WarningPenalty} ELSE 0 END " +
        $"- CASE WHEN {IsOverLimit} THEN {QualityRules.ErrorPenalty} ELSE 0 END) END";

    public static int Bit(QualityRule rule) => 1 << (int)rule;

    /// <summary>
    /// Bits of the single-record rules that are errors.
    /// </summary>
    public static int ErrorBits => QualityRules.RecordRules
        .Where(rule => QualityRules.SeverityOf(rule) == IssueSeverity.Error)
        .Aggregate(0, (bits, rule) => bits | Bit(rule));

    /// <summary>
    /// The WHERE condition for a quality filter, or <c>null</c> for none.
    /// </summary>
    public static string? Condition(QualityFilter filter) => filter switch
    {
        QualityFilter.Issues => $"({Score}) < 100",
        QualityFilter.Errors => $"(a.RecordId IS NOT NULL AND ((a.IssueFlags & {ErrorBits}) <> 0 OR {IsOverLimit}))",
        QualityFilter.OverContextLimit => $"(a.RecordId IS NOT NULL AND {IsOverLimit})",
        QualityFilter.Duplicates => IsDuplicate,
        QualityFilter.PersonalData => $"(a.RecordId IS NOT NULL AND (a.IssueFlags & {Bit(QualityRule.PersonalData)}) <> 0)",
        QualityFilter.NearDuplicates => $"(a.RecordId IS NOT NULL AND {IsNearDuplicate})",
        _ => null,
    };

    public static int Archived => (int)RecordStatus.Archived;
}
