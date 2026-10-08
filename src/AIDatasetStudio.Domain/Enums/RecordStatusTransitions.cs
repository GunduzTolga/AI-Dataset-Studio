namespace AIDatasetStudio.Domain.Enums;

/// <summary>
/// Allowed status changes (SDD §5.2). Besides the forward path, speed review (§6.2) jumps
/// from Draft straight to Approved, edited records go back to review, and archived records
/// can be restored.
/// </summary>
public static class RecordStatusTransitions
{
    private static readonly Dictionary<RecordStatus, RecordStatus[]> Allowed = new()
    {
        [RecordStatus.Draft] = [RecordStatus.InReview, RecordStatus.Approved, RecordStatus.Archived],
        [RecordStatus.InReview] = [RecordStatus.Draft, RecordStatus.Approved, RecordStatus.Archived],
        [RecordStatus.Approved] = [RecordStatus.InReview, RecordStatus.Archived],
        [RecordStatus.Archived] = [RecordStatus.Draft],
    };

    public static bool CanTransition(RecordStatus from, RecordStatus to) =>
        from == to || (Allowed.TryGetValue(from, out var targets) && targets.Contains(to));

    public static IReadOnlyList<RecordStatus> AllowedTargets(RecordStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];
}
