using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Application.Records;

/// <summary>
/// A record to add in bulk.
/// </summary>
public sealed record NewRecord(RecordPayload Payload, RecordLineage Lineage);

/// <summary>
/// Narrows a record list. Empty criteria match everything.
/// </summary>
/// <param name="Statuses">Only these statuses; <c>null</c> or empty for all.</param>
/// <param name="SearchText">Full-text search over all record text; <c>null</c> or blank for none.</param>
public sealed record RecordFilter(IReadOnlySet<RecordStatus>? Statuses = null, string? SearchText = null)
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
public sealed record RecordSummary(
    Guid Id,
    long Ordinal,
    RecordStatus Status,
    string Preview,
    DateTimeOffset UpdatedAt)
{
    public RecordCursor Cursor => new(Ordinal, Id);
}

/// <param name="Items">The rows of this page.</param>
/// <param name="Next">Cursor for the following page, or <c>null</c> when this is the last page.</param>
public sealed record RecordPage(IReadOnlyList<RecordSummary> Items, RecordCursor? Next);

public sealed record RecordDetails(
    Guid Id,
    Guid DatasetId,
    long Ordinal,
    RecordStatus Status,
    RecordPayload Payload,
    string ContentHash,
    RecordLineage Lineage,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

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
/// A record's status, and optionally its content, at one moment; undo and redo put records
/// back into such states.
/// </summary>
/// <param name="Payload">The content, or <c>null</c> to leave the content as it is.</param>
public sealed record RecordState(Guid RecordId, RecordStatus Status, RecordPayload? Payload = null);

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
