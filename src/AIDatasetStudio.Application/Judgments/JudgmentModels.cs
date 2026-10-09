using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Judgments;

/// <summary>
/// A model's judgment of a record (SDD §17.7), kept until the record is judged again.
/// </summary>
/// <param name="ContentHash">The content that was judged; when the record changes, the judgment is out of date.</param>
/// <param name="Model">The model that judged.</param>
public sealed record RecordJudgment(
    Guid RecordId,
    string ContentHash,
    int Score,
    string Reason,
    JudgeFlags Flags,
    string Model,
    DateTimeOffset JudgedAt);

/// <summary>
/// What a model said about the answers of two records that ask the same thing.
/// </summary>
/// <param name="RecordHash">The content of <paramref name="RecordId"/> that was compared.</param>
/// <param name="OtherHash">The content of <paramref name="OtherId"/> that was compared.</param>
public sealed record RecordConflict(
    Guid RecordId,
    Guid OtherId,
    string RecordHash,
    string OtherHash,
    bool Contradicts,
    string Reason,
    string Model,
    DateTimeOffset CheckedAt);

/// <summary>
/// A contradiction a model found between a record and another one, both still as they were compared.
/// </summary>
public sealed record ContradictionDetails(Guid OtherId, long OtherOrdinal, string Reason, string Model, DateTimeOffset CheckedAt);

/// <summary>
/// A record's judgment and the contradictions found with other records.
/// </summary>
/// <param name="Judgment">The last judgment, or <c>null</c> when the record was never judged.</param>
/// <param name="IsCurrent">Whether the record is still as it was judged.</param>
/// <param name="Contradictions">Contradictions with records that are not archived; only those still as they were compared.</param>
public sealed record JudgmentDetails(RecordJudgment? Judgment, bool IsCurrent, IReadOnlyList<ContradictionDetails> Contradictions)
{
    public static JudgmentDetails None { get; } = new(null, false, []);
}

/// <summary>
/// Another record, not archived, that asks the same or nearly the same question (or belongs to the
/// same family) and answers differently.
/// </summary>
public sealed record SiblingRecord(Guid Id, long Ordinal, string ContentHash, RecordPayload Payload);

/// <summary>
/// The judgments of a dataset's records that are not archived.
/// </summary>
/// <param name="RecordCount">Records that are not archived.</param>
/// <param name="JudgedCount">Records judged as they are now.</param>
/// <param name="OutdatedCount">Records changed since they were judged.</param>
/// <param name="ScoreCounts">Records judged as they are now, per score: index 0 for 1 … index 4 for 5.</param>
/// <param name="Average">The mean current score; <c>null</c> when none is judged.</param>
/// <param name="UngroundedCount">Current judgments that found something the source passage does not say.</param>
/// <param name="PreferenceCount">Current judgments of preference pairs whose rejected answer is as good or better.</param>
/// <param name="ContradictionCount">Records with a current contradiction.</param>
public sealed record JudgmentSummary(
    long RecordCount,
    long JudgedCount,
    long OutdatedCount,
    IReadOnlyList<long> ScoreCounts,
    double? Average,
    long UngroundedCount,
    long PreferenceCount,
    long ContradictionCount)
{
    public long NotJudgedCount => RecordCount - JudgedCount;

    public long LowCount => ScoreCounts.Take(Judging.LowScore).Sum();

    public bool HasJudgments => JudgedCount > 0 || OutdatedCount > 0;
}
