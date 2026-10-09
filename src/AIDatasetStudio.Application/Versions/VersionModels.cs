using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Versions;

/// <summary>
/// A frozen version of a dataset (SDD §14).
/// </summary>
/// <param name="Number">"1.2.0"; shown as "v1.2.0".</param>
/// <param name="Tokenizer">The tokenizer its token counts were made with.</param>
/// <param name="ContextLength">The context length its exports check against.</param>
/// <param name="RecordCount">All records, archived ones included.</param>
public sealed record VersionInfo(
    Guid Id,
    Guid DatasetId,
    string Number,
    string? Description,
    string Tokenizer,
    int ContextLength,
    long RecordCount,
    long ApprovedCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// How a record differs between an older and a newer state of a dataset.
/// </summary>
public enum DifferenceKind
{
    /// <summary>Only in the newer state.</summary>
    Added,

    /// <summary>Only in the older state (deleted since).</summary>
    Removed,

    /// <summary>The content changed (the status may have too).</summary>
    Modified,

    /// <summary>Same content, another status (the tags may have changed too).</summary>
    StatusChanged,

    /// <summary>Same content and status, other tags.</summary>
    TagsChanged,
}

/// <summary>
/// One record that differs between two states of a dataset.
/// </summary>
/// <param name="Ordinal">The record's number in the list.</param>
/// <param name="Preview">One line of the record: the newer content, or the older one when removed.</param>
/// <param name="OldStatus">The status in the older state; <c>null</c> when added.</param>
/// <param name="NewStatus">The status in the newer state; <c>null</c> when removed.</param>
/// <param name="OldTagIds">The tags in the older state; <c>null</c> when added.</param>
/// <param name="NewTagIds">The tags in the newer state; <c>null</c> when removed.</param>
public sealed record RecordDifference(
    Guid RecordId,
    DifferenceKind Kind,
    long Ordinal,
    string Preview,
    RecordStatus? OldStatus,
    RecordStatus? NewStatus,
    IReadOnlyList<Guid>? OldTagIds = null,
    IReadOnlyList<Guid>? NewTagIds = null)
{
    /// <summary>
    /// The tags differ between the two states (both exist).
    /// </summary>
    public bool TagsDiffer => OldTagIds is not null && NewTagIds is not null && !OldTagIds.Order().SequenceEqual(NewTagIds.Order());

    // Records compare collections by reference; compare the tags by value instead.
    public bool Equals(RecordDifference? other) =>
        other is not null
        && RecordId == other.RecordId && Kind == other.Kind && Ordinal == other.Ordinal && Preview == other.Preview
        && OldStatus == other.OldStatus && NewStatus == other.NewStatus
        && SameTags(OldTagIds, other.OldTagIds) && SameTags(NewTagIds, other.NewTagIds);

    public override int GetHashCode() => HashCode.Combine(RecordId, Kind, Ordinal, OldStatus, NewStatus);

    // No tags and a missing side compare equal: neither has tags.
    private static bool SameTags(IReadOnlyList<Guid>? a, IReadOnlyList<Guid>? b) => (a ?? []).Order().SequenceEqual((b ?? []).Order());
}

/// <summary>
/// The records that differ between two states of a dataset, in list order.
/// </summary>
/// <param name="Unchanged">Records with the same content and status in both.</param>
public sealed record VersionComparison(IReadOnlyList<RecordDifference> Differences, long Unchanged)
{
    public long Count(DifferenceKind kind) => Differences.Count(d => d.Kind == kind);
}

/// <summary>
/// What going back to a version changed, with what undo and redo need.
/// </summary>
/// <param name="Number">The version gone back to.</param>
/// <param name="Before">The previous state of every record that changed (content only where it changed).</param>
/// <param name="After">The same records as the version has them.</param>
/// <param name="Recreated">Records of the version that had been deleted and were brought back.</param>
/// <param name="ContentRestored">Records whose content went back.</param>
/// <param name="StatusRestored">Records whose status or tags went back, with the same content.</param>
/// <param name="Archived">Records the version does not have; they are archived, not deleted.</param>
public sealed record RollbackResult(
    string Number,
    IReadOnlyList<RecordState> Before,
    IReadOnlyList<RecordState> After,
    IReadOnlyList<RecordSnapshot> Recreated,
    int ContentRestored,
    int StatusRestored,
    int Archived)
{
    public int Changed => Before.Count + Recreated.Count;
}
