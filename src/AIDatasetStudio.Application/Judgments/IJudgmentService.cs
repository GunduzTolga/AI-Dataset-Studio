namespace AIDatasetStudio.Application.Judgments;

/// <summary>
/// Judgments of records by a model and contradictions found between records (SDD §17.7). They are
/// kept per record with the content they were made of: a record changed since then shows its last
/// judgment as out of date, and filters and counts use only current ones. They go with their records.
/// </summary>
public interface IJudgmentService
{
    /// <summary>
    /// Keeps a judgment, replacing the record's previous one.
    /// </summary>
    Task SaveAsync(RecordJudgment judgment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps what a model said about two records, replacing an earlier comparison of the pair.
    /// </summary>
    Task SaveConflictAsync(RecordConflict conflict, CancellationToken cancellationToken = default);

    /// <returns>The record's judgment and contradictions; <see cref="JudgmentDetails.None"/> for a record without any.</returns>
    Task<JudgmentDetails> GetAsync(Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records to compare a record with: not archived, asking the same or nearly the same
    /// question (as analysed) or of the same family, with another answer, and not compared with
    /// it as both are now. The first ones in the dataset come first.
    /// </summary>
    Task<IReadOnlyList<SiblingRecord>> FindSiblingsAsync(Guid recordId, int max, CancellationToken cancellationToken = default);

    Task<JudgmentSummary> GetSummaryAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that are not archived and not judged as they are now, in dataset order.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindNotJudgedAsync(Guid datasetId, CancellationToken cancellationToken = default);
}
