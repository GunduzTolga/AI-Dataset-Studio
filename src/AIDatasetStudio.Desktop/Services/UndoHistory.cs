using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// A finished change to records that can be undone and redone.
/// </summary>
public interface IUndoableAction
{
    /// <summary>
    /// The dataset whose records changed.
    /// </summary>
    Guid DatasetId { get; }

    /// <summary>
    /// The records that changed, to show them after undo or redo.
    /// </summary>
    IReadOnlyList<Guid> RecordIds { get; }

    /// <summary>
    /// What the change did, e.g. "Approve #12" or "Delete 3 records".
    /// </summary>
    string Describe(Localizer localizer);

    /// <returns>The number of records put back; 0 if none of them exist any more.</returns>
    Task<int> UndoAsync(IRecordCommandService commands);

    /// <returns>The number of records changed again; 0 if none of them exist any more.</returns>
    Task<int> RedoAsync(IRecordCommandService commands);
}

/// <summary>
/// Undo and redo stacks for record changes of the open workspace (SDD §6.2: single edits and
/// batch actions). The history lives for the session only and is cleared when the workspace changes.
/// </summary>
public sealed class UndoHistory : ObservableObject
{
    public const int Capacity = 100;

    private readonly LinkedList<IUndoableAction> _undo = new();
    private readonly Stack<IUndoableAction> _redo = new();

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public IUndoableAction? NextUndo => _undo.Last?.Value;

    public IUndoableAction? NextRedo => _redo.TryPeek(out var action) ? action : null;

    /// <summary>
    /// Records a new change; it can be undone next, and nothing can be redone any more.
    /// </summary>
    public void Push(IUndoableAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _undo.AddLast(action);
        if (_undo.Count > Capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        Notify();
    }

    /// <summary>
    /// Moves the last change to the redo stack after it was undone.
    /// </summary>
    public void MarkUndone(IUndoableAction action)
    {
        if (_undo.Last?.Value == action)
        {
            _undo.RemoveLast();
            _redo.Push(action);
            Notify();
        }
    }

    /// <summary>
    /// Moves the next redo back to the undo stack after it was redone.
    /// </summary>
    public void MarkRedone(IUndoableAction action)
    {
        if (NextRedo == action)
        {
            _redo.Pop();
            _undo.AddLast(action);
            Notify();
        }
    }

    /// <summary>
    /// Drops a change that can no longer be applied (its records are gone).
    /// </summary>
    public void Discard(IUndoableAction action)
    {
        if (_undo.Last?.Value == action)
        {
            _undo.RemoveLast();
        }
        else if (NextRedo == action)
        {
            _redo.Pop();
        }

        Notify();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(NextUndo));
        OnPropertyChanged(nameof(NextRedo));
    }
}

/// <summary>
/// Records moved to another status; undo puts each one back where it was.
/// </summary>
/// <param name="Ordinal">The record number when a single record changed, for the description.</param>
public sealed record StatusChangeAction(Guid DatasetId, IReadOnlyList<RecordStatusUpdate> Changes, RecordStatus Target, long? Ordinal)
    : IUndoableAction
{
    public IReadOnlyList<Guid> RecordIds => Changes.Select(c => c.RecordId).ToList();

    public string Describe(Localizer localizer) =>
        localizer.Format(HistoryText.StatusKey(Target), HistoryText.Subject(localizer, Ordinal, Changes.Count));

    public Task<int> UndoAsync(IRecordCommandService commands) =>
        commands.RestoreStatesAsync(Changes.Select(c => new RecordState(c.RecordId, c.From)).ToList());

    public Task<int> RedoAsync(IRecordCommandService commands) =>
        commands.RestoreStatesAsync(Changes.Select(c => new RecordState(c.RecordId, c.To)).ToList());
}

/// <summary>
/// A record's content was edited (which also sent it to review).
/// </summary>
public sealed record EditAction(Guid DatasetId, long Ordinal, RecordState Before, RecordState After) : IUndoableAction
{
    public IReadOnlyList<Guid> RecordIds => [Before.RecordId];

    public string Describe(Localizer localizer) =>
        localizer.Format("History_Edit", HistoryText.Subject(localizer, Ordinal, 1));

    public Task<int> UndoAsync(IRecordCommandService commands) => commands.RestoreStatesAsync([Before]);

    public Task<int> RedoAsync(IRecordCommandService commands) => commands.RestoreStatesAsync([After]);
}

/// <summary>
/// Records were deleted; undo brings them back exactly as they were.
/// </summary>
public sealed record DeleteAction(Guid DatasetId, IReadOnlyList<RecordSnapshot> Snapshots) : IUndoableAction
{
    public IReadOnlyList<Guid> RecordIds => Snapshots.Select(s => s.Id).ToList();

    public string Describe(Localizer localizer) =>
        localizer.Format("History_Delete", HistoryText.Subject(localizer, Snapshots.Count == 1 ? Snapshots[0].Ordinal : null, Snapshots.Count));

    public Task<int> UndoAsync(IRecordCommandService commands) => commands.RestoreAsync(Snapshots);

    public Task<int> RedoAsync(IRecordCommandService commands) => commands.DeleteAsync(RecordIds);
}

internal static class HistoryText
{
    public static string StatusKey(RecordStatus target) => target switch
    {
        RecordStatus.Approved => "History_Approve",
        RecordStatus.InReview => "History_SendToReview",
        RecordStatus.Archived => "History_Archive",
        _ => "History_Restore",
    };

    /// <summary>
    /// "#12" for one record, "3 records" for several.
    /// </summary>
    public static string Subject(Localizer localizer, long? ordinal, int count) =>
        count == 1 && ordinal is { } single ? localizer.Format("History_One", single) : localizer.Format("History_Many", count);
}
