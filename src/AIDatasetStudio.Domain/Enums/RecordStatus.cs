namespace AIDatasetStudio.Domain.Enums;

/// <summary>
/// Review lifecycle of a record. See SDD §5.2 and <see cref="RecordStatusTransitions"/>.
/// </summary>
/// <remarks>
/// Values are persisted as integers; never renumber existing members.
/// </remarks>
public enum RecordStatus
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    ExportReady = 3,
    Archived = 4,
}
