using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// A change to records, so the list, the tree and the inspector can follow it.
/// </summary>
public abstract record RecordChange(Guid DatasetId);

public sealed record StatusesChanged(Guid DatasetId, IReadOnlyList<RecordStatusUpdate> Changes) : RecordChange(DatasetId);

public sealed record ContentChanged(Guid DatasetId, Guid RecordId, string Preview, RecordStatus Status) : RecordChange(DatasetId);

public sealed record RecordsDeleted(Guid DatasetId, IReadOnlyList<Guid> RecordIds) : RecordChange(DatasetId);

/// <summary>
/// Records were put back into earlier states by undo or redo; views reload them.
/// </summary>
public sealed record RecordsReplaced(Guid DatasetId, IReadOnlyList<Guid> RecordIds) : RecordChange(DatasetId);

/// <param name="Action">The step that was undone or redone.</param>
/// <param name="Count">Records affected; 0 if they no longer exist and the step was dropped.</param>
public sealed record ReplayResult(IUndoableAction Action, int Count);

/// <summary>
/// The record changes a user makes in the UI. Each one is recorded in <see cref="UndoHistory"/>
/// and announced through <see cref="Changed"/>.
/// </summary>
public sealed class RecordOperations(IRecordCommandService commands, IRecordQueryService queries, IVersionService versions, UndoHistory history)
{
    public event EventHandler<RecordChange>? Changed;

    public UndoHistory History => history;

    /// <param name="datasetId">The dataset of the records.</param>
    /// <param name="recordIds">The records to change; those the lifecycle does not allow are skipped.</param>
    /// <param name="status">The new status.</param>
    /// <param name="ordinal">The record number when a single record is changed, for the history.</param>
    public async Task<StatusChangeResult> SetStatusAsync(Guid datasetId, IReadOnlyCollection<Guid> recordIds, RecordStatus status, long? ordinal = null)
    {
        var result = await commands.SetStatusAsync(recordIds, status);
        if (result.Changed > 0)
        {
            history.Push(new StatusChangeAction(datasetId, result.Changes, status, recordIds.Count == 1 ? ordinal : null));
            Changed?.Invoke(this, new StatusesChanged(datasetId, result.Changes));
        }

        return result;
    }

    /// <summary>
    /// Saves edited content; the record goes to review (SDD §5.2).
    /// </summary>
    /// <returns><c>false</c> if the content did not change.</returns>
    public async Task<bool> SaveAsync(RecordDetails before, RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (!await commands.UpdatePayloadAsync(before.Id, payload))
        {
            return false;
        }

        history.Push(new EditAction(
            before.DatasetId,
            before.Ordinal,
            new RecordState(before.Id, before.Status, before.Payload),
            new RecordState(before.Id, RecordStatus.InReview, payload)));
        Changed?.Invoke(this, new ContentChanged(before.DatasetId, before.Id, payload.GetPreviewText(DatasetRecord.PreviewMaxLength), RecordStatus.InReview));
        return true;
    }

    /// <summary>
    /// Deletes records, keeping copies so the deletion can be undone.
    /// </summary>
    public async Task<int> DeleteAsync(Guid datasetId, IReadOnlyCollection<Guid> recordIds)
    {
        var snapshots = await queries.GetSnapshotsAsync(recordIds);
        var deleted = await commands.DeleteAsync(recordIds);
        if (deleted > 0)
        {
            history.Push(new DeleteAction(datasetId, snapshots));
            Changed?.Invoke(this, new RecordsDeleted(datasetId, snapshots.Select(s => s.Id).ToList()));
        }

        return deleted;
    }

    /// <summary>
    /// Makes the dataset match a version again (SDD §14.2); one step in the history.
    /// </summary>
    public async Task<RollbackResult> RollbackAsync(Guid datasetId, Guid versionId)
    {
        var result = await versions.RollbackAsync(versionId);
        if (result.Changed > 0)
        {
            var action = new RollbackAction(datasetId, result.Number, result.Before, result.After, result.Recreated);
            history.Push(action);
            Changed?.Invoke(this, new RecordsReplaced(datasetId, action.RecordIds));
        }

        return result;
    }

    /// <returns>What was undone, or <c>null</c> if there is nothing to undo.</returns>
    public Task<ReplayResult?> UndoAsync() => ReplayAsync(history.NextUndo, action => action.UndoAsync(commands), history.MarkUndone);

    /// <returns>What was redone, or <c>null</c> if there is nothing to redo.</returns>
    public Task<ReplayResult?> RedoAsync() => ReplayAsync(history.NextRedo, action => action.RedoAsync(commands), history.MarkRedone);

    private async Task<ReplayResult?> ReplayAsync(IUndoableAction? action, Func<IUndoableAction, Task<int>> replay, Action<IUndoableAction> done)
    {
        if (action is null)
        {
            return null;
        }

        int count;
        try
        {
            count = await replay(action);
        }
        catch
        {
            // The step cannot be applied (e.g. its dataset was deleted); keeping it would fail again.
            history.Discard(action);
            throw;
        }

        if (count == 0)
        {
            history.Discard(action);
        }
        else
        {
            done(action);
            Changed?.Invoke(this, new RecordsReplaced(action.DatasetId, action.RecordIds));
        }

        return new ReplayResult(action, count);
    }
}
