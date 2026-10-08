using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Domain.Entities;

/// <summary>
/// A single training example (SDD §5.1). Records are their own aggregate so that a
/// dataset with 100k+ records never has to be loaded as a whole.
/// </summary>
public sealed class DatasetRecord
{
    private readonly List<Tag> _tags = [];

    private DatasetRecord()
    {
        Payload = null!;
        ContentHash = string.Empty;
        Lineage = null!;
    }

    public Guid Id { get; private set; }

    public Guid DatasetId { get; private set; }

    /// <summary>
    /// Mirrors the dataset type; stored so records can be filtered without a join.
    /// </summary>
    public DatasetType Type { get; private set; }

    public RecordStatus Status { get; private set; }

    /// <summary>
    /// Stable position of the record inside its dataset (grid and export order).
    /// </summary>
    public long Ordinal { get; private set; }

    public RecordPayload Payload { get; private set; }

    /// <summary>
    /// SHA-256 of <see cref="Payload"/>; see <see cref="RecordPayload.ComputeContentHash"/>.
    /// </summary>
    public string ContentHash { get; private set; }

    /// <summary>
    /// Free-form metadata as a JSON object.
    /// </summary>
    public string MetadataJson { get; private set; } = "{}";

    public RecordLineage Lineage { get; private set; }

    public IReadOnlyCollection<Tag> Tags => _tags;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DatasetRecord Create(
        Dataset dataset,
        RecordPayload payload,
        RecordLineage lineage,
        long ordinal,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(lineage);
        EnsureMatchesType(dataset.Type, payload);

        return new DatasetRecord
        {
            Id = Guid.CreateVersion7(now),
            DatasetId = dataset.Id,
            Type = dataset.Type,
            Status = RecordStatus.Draft,
            Ordinal = ordinal,
            Payload = payload,
            ContentHash = payload.ComputeContentHash(),
            Lineage = lineage,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Replaces the content. A changed record goes (back) to <see cref="RecordStatus.InReview"/>,
    /// because the SDD defines InReview as "records an editor has modified" (§5.2).
    /// </summary>
    /// <returns><c>true</c> if the content actually changed.</returns>
    public bool UpdatePayload(RecordPayload payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payload);
        EnsureMatchesType(Type, payload);

        if (Status == RecordStatus.Archived)
        {
            throw new DomainException("Archived records cannot be edited; restore the record first.");
        }

        var hash = payload.ComputeContentHash();
        if (hash == ContentHash)
        {
            return false;
        }

        Payload = payload;
        ContentHash = hash;
        Status = RecordStatus.InReview;
        UpdatedAt = now;
        return true;
    }

    public void ChangeStatus(RecordStatus status, DateTimeOffset now)
    {
        if (status == Status)
        {
            return;
        }

        if (!RecordStatusTransitions.CanTransition(Status, status))
        {
            throw new DomainException($"A record cannot move from {Status} to {status}.");
        }

        Status = status;
        UpdatedAt = now;
    }

    public bool AddTag(Tag tag, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (_tags.Any(t => t.Id == tag.Id))
        {
            return false;
        }

        _tags.Add(tag);
        UpdatedAt = now;
        return true;
    }

    public bool RemoveTag(Guid tagId, DateTimeOffset now)
    {
        if (_tags.RemoveAll(t => t.Id == tagId) == 0)
        {
            return false;
        }

        UpdatedAt = now;
        return true;
    }

    private static void EnsureMatchesType(DatasetType expected, RecordPayload payload)
    {
        if (payload.Type != expected)
        {
            throw new DomainException($"A {payload.Type} payload cannot be stored in a {expected} dataset.");
        }
    }
}
