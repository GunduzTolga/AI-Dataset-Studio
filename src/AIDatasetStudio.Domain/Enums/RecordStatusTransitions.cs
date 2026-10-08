namespace AIDatasetStudio.Domain.Enums;

/// <summary>
/// Allowed status changes. The SDD diagram (§5.2) is the forward path; the review
/// shortcuts (§6.2) additionally need jumps such as Draft → Approved, and editing
/// or restoring records needs a few backward moves.
/// </summary>
public static class RecordStatusTransitions
{
    private static readonly Dictionary<RecordStatus, RecordStatus[]> Allowed = new()
    {
        [RecordStatus.Draft] = [RecordStatus.InReview, RecordStatus.Approved, RecordStatus.Archived],
        [RecordStatus.InReview] = [RecordStatus.Draft, RecordStatus.Approved, RecordStatus.Archived],
        [RecordStatus.Approved] = [RecordStatus.InReview, RecordStatus.ExportReady, RecordStatus.Archived],
        [RecordStatus.ExportReady] = [RecordStatus.InReview, RecordStatus.Approved, RecordStatus.Archived],
        [RecordStatus.Archived] = [RecordStatus.Draft],
    };

    public static bool CanTransition(RecordStatus from, RecordStatus to) =>
        from == to || (Allowed.TryGetValue(from, out var targets) && targets.Contains(to));

    public static IReadOnlyList<RecordStatus> AllowedTargets(RecordStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];
}
