using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.ValueObjects;

/// <summary>
/// Provenance of a record (SDD §2.4): where it came from and who or what produced it.
/// </summary>
/// <param name="SourceKind">The kind of source the record was created from.</param>
/// <param name="SourceReference">Free-form pointer into the source, e.g. "faq.csv#row=42".</param>
/// <param name="CreatedBy">The person who created or imported the record, if known.</param>
/// <param name="GeneratedByModel">The model that generated the content, e.g. "gpt-4o", if any.</param>
public sealed record RecordLineage(
    RecordSourceKind SourceKind,
    string? SourceReference = null,
    string? CreatedBy = null,
    string? GeneratedByModel = null)
{
    public static RecordLineage Manual(string? createdBy = null) => new(RecordSourceKind.Manual, CreatedBy: createdBy);

    public bool IsGeneratedByAi => GeneratedByModel is not null;
}
