namespace AIDatasetStudio.Application.Analysis;

/// <summary>
/// Narrows a record list by analysis results.
/// </summary>
public enum QualityFilter
{
    None,

    /// <summary>Any issue (score below 100).</summary>
    Issues,

    /// <summary>At least one error.</summary>
    Errors,

    OverContextLimit,

    /// <summary>Records whose content also exists elsewhere in the dataset.</summary>
    Duplicates,

    PersonalData,
}

/// <param name="UpperBound">Exclusive upper token bound; <see cref="int.MaxValue"/> for the last bucket.</param>
public sealed record TokenBucket(int UpperBound, long Count);

/// <summary>
/// Token and quality figures of a dataset (SDD §7.3, §8.1).
/// </summary>
/// <param name="RecordCount">All records.</param>
/// <param name="AnalyzedCount">Records with up-to-date analysis results.</param>
/// <param name="Score">Mean score of the analysed records that are not archived; <c>null</c> before any analysis.</param>
/// <param name="IssueCounts">Records per rule; rules without records are left out.</param>
public sealed record DatasetQualitySummary(
    long RecordCount,
    long AnalyzedCount,
    int? Score,
    long TotalTokens,
    int MaxTokens,
    long OverLimitCount,
    IReadOnlyList<TokenBucket> Histogram,
    IReadOnlyDictionary<QualityRule, long> IssueCounts)
{
    public long PendingCount => RecordCount - AnalyzedCount;
}

/// <summary>
/// The analysis of one record, including the issues that compare it with other records.
/// </summary>
/// <param name="Tokens">Token counts, or <c>null</c> when the record has not been analysed yet.</param>
public sealed record RecordQuality(TokenCounts? Tokens, int? Score, IReadOnlyList<QualityIssue> Issues)
{
    public bool IsAnalyzed => Tokens is not null;
}

/// <summary>
/// Token counting and quality checks over datasets of the open workspace (SDD §7, §8, §9).
/// Results are stored per record and recomputed when the content, the dataset's tokenizer,
/// the rules or the custom personal-data terms change.
/// </summary>
public interface IRecordAnalysisService
{
    /// <summary>
    /// Records of the dataset without up-to-date results.
    /// </summary>
    Task<long> CountPendingAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyses the records of the dataset that have no up-to-date results.
    /// </summary>
    /// <param name="datasetId">The dataset.</param>
    /// <param name="progress">Receives the number of records analysed so far.</param>
    /// <param name="cancellationToken">Stops after the current batch; finished batches are kept.</param>
    /// <returns>The number of records analysed.</returns>
    Task<int> AnalyzeAsync(Guid datasetId, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyses the given records now, e.g. right after they were edited.
    /// </summary>
    Task AnalyzeRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default);

    Task<DatasetQualitySummary> GetSummaryAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <returns>The record's analysis; <c>null</c> if the record does not exist.</returns>
    Task<RecordQuality?> GetRecordQualityAsync(Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records with an issue, for clean-up actions (SDD §8.3). For <see cref="QualityRule.Duplicate"/>
    /// the first record of each group is left out, so archiving the result keeps one copy.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindAsync(Guid datasetId, QualityRule rule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Words to treat as personal data in this workspace (company names, code names…).
    /// </summary>
    Task<IReadOnlyList<string>> GetCustomTermsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the custom terms; every record is analysed again afterwards.
    /// </summary>
    Task SetCustomTermsAsync(IReadOnlyList<string> terms, CancellationToken cancellationToken = default);
}
