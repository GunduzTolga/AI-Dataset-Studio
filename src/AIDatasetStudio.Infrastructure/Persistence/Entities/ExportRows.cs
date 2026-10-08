using AIDatasetStudio.Application.Export;

namespace AIDatasetStudio.Infrastructure.Persistence.Entities;

/// <summary>
/// The export options last used for a dataset (SDD §13.2 export profile), including the split
/// seed, so the same files can be produced again (SDD §10.1).
/// </summary>
internal sealed class ExportProfileRow
{
    public Guid DatasetId { get; set; }

    public ExportFormat TargetFormat { get; set; }

    /// <summary>
    /// The <see cref="ExportOptions"/> as JSON.
    /// </summary>
    public string Settings { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A finished export (SDD §18 ExportHistories): what was written where, with which settings.
/// </summary>
internal sealed class ExportHistoryRow
{
    public Guid Id { get; set; }

    public Guid DatasetId { get; set; }

    /// <summary>
    /// The version exported, or <c>null</c> for the working copy. Not a foreign key: the history
    /// keeps the entry (and <see cref="VersionNumber"/>) when the version is deleted.
    /// </summary>
    public Guid? VersionId { get; set; }

    /// <summary>
    /// "1.2.0" when a version was exported.
    /// </summary>
    public string? VersionNumber { get; set; }

    public ExportFormat TargetFormat { get; set; }

    /// <summary>
    /// The <see cref="ExportOptions"/> as JSON.
    /// </summary>
    public string Settings { get; set; } = "{}";

    /// <summary>
    /// The written files as JSON: [{"part": "Train", "path": "...", "count": 123}].
    /// </summary>
    public string Files { get; set; } = "[]";

    public long RecordCount { get; set; }

    public DateTimeOffset ExportedAt { get; set; }
}
