namespace AIDatasetStudio.Domain.Enums;

/// <summary>
/// Review lifecycle of a record. See SDD §5.2 and <see cref="RecordStatusTransitions"/>.
/// </summary>
/// <remarks>
/// Whether a record is ready for export is not a status: it is computed per export profile
/// (token limit, PII checks) because it changes whenever those settings change.
/// Values are persisted as integers; never renumber existing members.
/// </remarks>
public enum RecordStatus
{
    Draft = 0,
    InReview = 1,
    Approved = 2,
    Archived = 3,
}
