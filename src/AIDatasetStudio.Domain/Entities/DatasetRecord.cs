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

    public const int PreviewMaxLength = 200;

    private DatasetRecord()
    {
        Payload = null!;
        ContentHash = string.Empty;
        Preview = string.Empty;
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
    /// Single-line summary of <see cref="Payload"/>, stored so record lists never load full payloads.
    /// </summary>
    public string Preview { get; private set; }

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
            Preview = payload.GetPreviewText(PreviewMaxLength),
            Lineage = lineage,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Brings back a deleted record exactly as it was (undo of a delete): same id, position,
    /// status, lineage and timestamps.
    /// </summary>
    public static DatasetRecord Recreate(
        Dataset dataset,
        Guid id,
        long ordinal,
        RecordStatus status,
        RecordPayload payload,
        RecordLineage lineage,
        string metadataJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(lineage);
        EnsureMatchesType(dataset.Type, payload);

        return new DatasetRecord
        {
            Id = id,
            DatasetId = dataset.Id,
            Type = dataset.Type,
            Status = status,
            Ordinal = ordinal,
            Payload = payload,
            ContentHash = payload.ComputeContentHash(),
            Preview = payload.GetPreviewText(PreviewMaxLength),
            MetadataJson = string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson,
            Lineage = lineage,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
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
        Preview = payload.GetPreviewText(PreviewMaxLength);
        Status = RecordStatus.InReview;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Notes in the lineage that a model wrote (part of) the content (SDD §2.4).
    /// </summary>
    public void MarkGeneratedBy(string model) =>
        Lineage = Lineage with { GeneratedByModel = Guard.NotBlank(model, nameof(model), 200) };

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

    /// <summary>
    /// Puts the record back into an earlier state (undo and redo). The lifecycle rules of
    /// <see cref="ChangeStatus"/> and <see cref="UpdatePayload"/> do not apply: the earlier state
    /// was valid when it existed, and undoing "Draft → Approved" must be able to go back to Draft.
    /// </summary>
    /// <param name="status">The earlier status.</param>
    /// <param name="payload">The earlier content, or <c>null</c> to keep the current content.</param>
    /// <param name="now">The time of the change.</param>
    /// <param name="lineage">The earlier lineage, or <c>null</c> to keep the current one.</param>
    /// <returns><c>true</c> if the content was replaced.</returns>
    public bool RestoreState(RecordStatus status, RecordPayload? payload, DateTimeOffset now, RecordLineage? lineage = null)
    {
        if (lineage is not null)
        {
            Lineage = lineage;
        }

        var contentChanged = false;
        if (payload is not null)
        {
            EnsureMatchesType(Type, payload);
            var hash = payload.ComputeContentHash();
            if (hash != ContentHash)
            {
                Payload = payload;
                ContentHash = hash;
                Preview = payload.GetPreviewText(PreviewMaxLength);
                contentChanged = true;
            }
        }

        if (contentChanged || status != Status)
        {
            Status = status;
            UpdatedAt = now;
        }

        return contentChanged;
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

    /// <summary>
    /// Replaces the tags (undo, redo and going back to a version).
    /// </summary>
    /// <returns><c>true</c> if they changed.</returns>
    public bool SetTags(IReadOnlyCollection<Tag> tags, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var wanted = tags.DistinctBy(t => t.Id).ToList();
        if (wanted.Count == _tags.Count && wanted.All(t => _tags.Any(existing => existing.Id == t.Id)))
        {
            return false;
        }

        _tags.Clear();
        _tags.AddRange(wanted);
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
