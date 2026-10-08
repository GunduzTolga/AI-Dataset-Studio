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

/// <param name="Changed">Records whose status changed.</param>
/// <param name="Skipped">Records left as they were because the transition is not allowed or they no longer exist.</param>
public sealed record StatusChangeResult(int Changed, int Skipped);
