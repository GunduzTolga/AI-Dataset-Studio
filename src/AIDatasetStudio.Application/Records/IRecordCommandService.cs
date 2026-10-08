using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Application.Records;

/// <summary>
/// Changes records of the open workspace. Every change keeps the search index in sync.
/// </summary>
public interface IRecordCommandService
{
    /// <summary>
    /// Appends a new draft record to the end of the dataset.
    /// </summary>
    Task<Guid> AddAsync(Guid datasetId, RecordPayload payload, RecordLineage lineage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends many draft records in a single transaction (all or nothing).
    /// </summary>
    /// <param name="datasetId">Target dataset.</param>
    /// <param name="records">Records to add, in order.</param>
    /// <param name="progress">Receives the number of records written so far.</param>
    /// <param name="cancellationToken">Cancels and rolls back the whole batch.</param>
    /// <returns>The number of records added.</returns>
    Task<int> AddRangeAsync(
        Guid datasetId,
        IEnumerable<NewRecord> records,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a record's content; a changed record goes back to review.
    /// </summary>
    /// <returns><c>true</c> if the content changed.</returns>
    Task<bool> UpdatePayloadAsync(Guid recordId, RecordPayload payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves records to <paramref name="status"/> where the lifecycle allows it.
    /// </summary>
    Task<StatusChangeResult> SetStatusAsync(IReadOnlyCollection<Guid> recordIds, RecordStatus status, CancellationToken cancellationToken = default);

    /// <returns>The number of records deleted.</returns>
    Task<int> DeleteAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts records back into earlier states (undo and redo), bypassing the lifecycle rules.
    /// Records that no longer exist are skipped.
    /// </summary>
    /// <returns>The number of records found.</returns>
    Task<int> RestoreStatesAsync(IReadOnlyCollection<RecordState> states, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings back deleted records with their ids, positions, statuses, lineage, timestamps and
    /// the tags that still exist (undo of a delete). Records that already exist are skipped.
    /// </summary>
    /// <returns>The number of records re-created.</returns>
    /// <exception cref="Common.NotFoundException">A record's dataset was deleted meanwhile.</exception>
    Task<int> RestoreAsync(IReadOnlyCollection<RecordSnapshot> snapshots, CancellationToken cancellationToken = default);
}
