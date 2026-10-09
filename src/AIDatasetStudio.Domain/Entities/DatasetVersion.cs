using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Domain.Entities;

/// <summary>
/// A frozen snapshot of a dataset (SDD §14): which records it had, in which revision, status
/// and order, and the tokenizer and context length they were checked with. A version never
/// changes after it is created; the dataset itself stays editable (ADR-030).
/// </summary>
public sealed class DatasetVersion
{
    public const int MaxDescriptionLength = 2_000;

    private DatasetVersion()
    {
        Number = string.Empty;
        Tokenizer = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid DatasetId { get; private set; }

    /// <summary>
    /// "1.2.0" (see <see cref="VersionNumber"/>); unique within the dataset.
    /// </summary>
    public string Number { get; private set; }

    /// <summary>
    /// What changed or what the version was made for, e.g. "First training run".
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// The dataset's tokenizer when the version was made; its stored token counts use it.
    /// </summary>
    public string Tokenizer { get; private set; }

    /// <summary>
    /// The dataset's context length when the version was made; exports of the version check against it.
    /// </summary>
    public int ContextLength { get; private set; }

    /// <summary>
    /// All records of the version, archived ones included.
    /// </summary>
    public long RecordCount { get; private set; }

    public long ApprovedCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static DatasetVersion Create(
        Dataset dataset,
        VersionNumber number,
        string? description,
        long recordCount,
        long approvedCount,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ArgumentOutOfRangeException.ThrowIfNegative(approvedCount);

        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmed is { Length: > MaxDescriptionLength })
        {
            throw new DomainException($"The description must be at most {MaxDescriptionLength} characters.");
        }

        return new DatasetVersion
        {
            Id = Guid.CreateVersion7(now),
            DatasetId = dataset.Id,
            Number = number.ToString(),
            Description = trimmed,
            Tokenizer = dataset.DefaultTokenizer,
            ContextLength = dataset.ContextLength,
            RecordCount = recordCount,
            ApprovedCount = approvedCount,
            CreatedAt = now,
        };
    }
}
