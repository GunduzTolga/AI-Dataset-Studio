namespace AIDatasetStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// Stored analysis of one record: token counts and the results of the single-record rules
/// (SDD §7.3, §8.1). Up to date only while <see cref="ContentHash"/>, <see cref="Tokenizer"/> and
/// <see cref="RulesVersion"/> match the record, its dataset and the rule set; otherwise it is
/// ignored and recomputed.
/// </summary>
internal sealed class RecordAnalysisRow
{
    public Guid RecordId { get; set; }

    /// <summary>
    /// Copied from the record so cross-record queries stay within one dataset's index range.
    /// </summary>
    public Guid DatasetId { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public string Tokenizer { get; set; } = string.Empty;

    public int RulesVersion { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int TotalTokens { get; set; }

    /// <summary>
    /// Fingerprint of the normalized question, for "same question, different answer".
    /// </summary>
    public string? QuestionKey { get; set; }

    /// <summary>
    /// MinHash signature of the normalized question (<see cref="Application.Analysis.NearDuplicates"/>).
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// The first record of this record's group of near-duplicate questions; <c>null</c> when it has
    /// none. Set by the grouping pass after analysis, from the questions of the whole dataset.
    /// </summary>
    public Guid? NearGroup { get; set; }

    /// <summary>
    /// Score from the single-record rules; cross-record and context-length penalties are applied when queried.
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// One bit per <see cref="Application.Analysis.QualityRule"/> found, for fast filtering.
    /// </summary>
    public int IssueFlags { get; set; }

    /// <summary>
    /// The issues as JSON: [{"rule": "...", "severity": "...", "detail": "..."}].
    /// </summary>
    public string Issues { get; set; } = "[]";

    public DateTimeOffset AnalyzedAt { get; set; }
}

/// <summary>
/// A workspace-wide setting, stored in the workspace file so it travels with the data.
/// </summary>
internal sealed class WorkspaceSetting
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
