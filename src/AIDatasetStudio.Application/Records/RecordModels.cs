using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Application.Records;

/// <summary>
/// A record to add in bulk.
/// </summary>
/// <param name="MetadataJson">The record's metadata, e.g. the passage of a document it was written from; <c>null</c> for none.</param>
public sealed record NewRecord(RecordPayload Payload, RecordLineage Lineage, string? MetadataJson = null);

/// <summary>
/// Narrows a record list. Empty criteria match everything.
/// </summary>
/// <param name="Statuses">Only these statuses; <c>null</c> or empty for all.</param>
/// <param name="SearchText">Full-text search over all record text; <c>null</c> or blank for none.</param>
/// <param name="Quality">Only records with these analysis results.</param>
/// <param name="TagId">Only records with this tag.</param>
/// <param name="Untagged">Only records without any tag.</param>
public sealed record RecordFilter(
    IReadOnlySet<RecordStatus>? Statuses = null,
    string? SearchText = null,
    QualityFilter Quality = QualityFilter.None,
    Guid? TagId = null,
    bool Untagged = false)
{
    public static RecordFilter None { get; } = new();
}

/// <summary>
/// Position in the stable (Ordinal, Id) order used for keyset pagination.
/// </summary>
public readonly record struct RecordCursor(long Ordinal, Guid Id);

/// <summary>
/// A lightweight list row; the full payload is loaded only for the selected record.
/// </summary>
/// <param name="TotalTokens">Content tokens with the dataset's tokenizer; <c>null</c> until analysed.</param>
/// <param name="Score">Quality score 0–100; <c>null</c> until analysed.</param>
/// <param name="IsOverContextLimit">More tokens than the dataset's context length.</param>
/// <param name="TagIds">The record's tags; <c>null</c> is read as none.</param>
/// <param name="JudgeScore">A model's score 1–5 of the record as it is now; <c>null</c> when not judged so (SDD §17.7).</param>
public sealed record RecordSummary(
    Guid Id,
    long Ordinal,
    RecordStatus Status,
    string Preview,
    DateTimeOffset UpdatedAt,
    int? TotalTokens = null,
    int? Score = null,
    bool IsOverContextLimit = false,
    IReadOnlyList<Guid>? TagIds = null,
    int? JudgeScore = null)
{
    public RecordCursor Cursor => new(Ordinal, Id);

    public IReadOnlyList<Guid> Tags => TagIds ?? [];
}

/// <param name="Items">The rows of this page.</param>
/// <param name="Next">Cursor for the following page, or <c>null</c> when this is the last page.</param>
public sealed record RecordPage(IReadOnlyList<RecordSummary> Items, RecordCursor? Next);

/// <param name="MetadataJson">Free metadata, e.g. the passage of a document the record was written from (<c>PassageSource</c>).</param>
public sealed record RecordDetails(
    Guid Id,
    Guid DatasetId,
    long Ordinal,
    RecordStatus Status,
    RecordPayload Payload,
    string ContentHash,
    RecordLineage Lineage,
    IReadOnlyList<Guid> TagIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string MetadataJson = "{}");

/// <summary>
/// One record moved from <see cref="From"/> to <see cref="To"/>.
/// </summary>
public sealed record RecordStatusUpdate(Guid RecordId, RecordStatus From, RecordStatus To);

/// <param name="Changes">Records whose status changed, with their previous status (for undo).</param>
/// <param name="Skipped">Records left as they were because the transition is not allowed or they no longer exist.</param>
public sealed record StatusChangeResult(IReadOnlyList<RecordStatusUpdate> Changes, int Skipped)
{
    public static StatusChangeResult None { get; } = new([], 0);

    public int Changed => Changes.Count;
}

/// <summary>
/// A record's status, and optionally its content and tags, at one moment; undo and redo put
/// records back into such states.
/// </summary>
/// <param name="Payload">The content, or <c>null</c> to leave the content as it is.</param>
/// <param name="TagIds">The tags, or <c>null</c> to leave them as they are; tags that no longer exist are left out.</param>
/// <param name="Lineage">The lineage, or <c>null</c> to leave it as it is (an edit with AI notes the model in it).</param>
public sealed record RecordState(
    Guid RecordId,
    RecordStatus Status,
    RecordPayload? Payload = null,
    IReadOnlyList<Guid>? TagIds = null,
    RecordLineage? Lineage = null);

/// <summary>
/// Everything needed to bring back a deleted record exactly as it was.
/// </summary>
public sealed record RecordSnapshot(
    Guid Id,
    Guid DatasetId,
    long Ordinal,
    RecordStatus Status,
    RecordPayload Payload,
    RecordLineage Lineage,
    string MetadataJson,
    IReadOnlyList<Guid> TagIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
