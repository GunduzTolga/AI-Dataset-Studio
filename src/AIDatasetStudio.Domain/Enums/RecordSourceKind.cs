namespace AIDatasetStudio.Domain.Enums;

/// <summary>
/// Where a record originally came from (lineage, SDD §2.4).
/// </summary>
/// <remarks>
/// Values are persisted as integers; never renumber existing members.
/// </remarks>
public enum RecordSourceKind
{
    Manual = 0,
    FileImport = 1,
    Synthetic = 2,
    DocumentChunk = 3,
}
