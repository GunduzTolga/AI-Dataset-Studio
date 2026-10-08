using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Versions;

public enum RecordFieldKind
{
    Instruction,
    Input,
    Output,
    SystemPrompt,
    Message,
    Prompt,
    Chosen,
    Rejected,
}

/// <summary>
/// One text of a record, in the order the editor shows them.
/// </summary>
/// <param name="Role">The speaker of a <see cref="RecordFieldKind.Message"/>.</param>
/// <param name="Turn">1-based position of a message in the conversation; 0 for other fields.</param>
public sealed record RecordField(RecordFieldKind Kind, string Text, ChatRole? Role = null, int Turn = 0);

/// <summary>
/// A field of a record in two versions; <see cref="Old"/> or <see cref="New"/> is <c>null</c>
/// when the field (a message, a system prompt or the whole record) exists on one side only.
/// </summary>
public sealed record FieldDifference(RecordField? Old, RecordField? New, TextDiff Diff)
{
    public RecordFieldKind Kind => (New ?? Old)!.Kind;

    public bool IsChanged => Old is null || New is null || Old.Role != New.Role || Diff.HasChanges;
}

/// <summary>
/// Lines up the fields of two versions of a record and compares their texts. Messages of a
/// conversation are matched as a sequence, so inserting a turn does not mark every later turn
/// as changed.
/// </summary>
public static class FieldDiff
{
    public static IReadOnlyList<RecordField> FieldsOf(RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload switch
        {
            InstructionPayload p =>
            [
                new(RecordFieldKind.Instruction, p.Instruction),
                new(RecordFieldKind.Input, p.Input),
                new(RecordFieldKind.Output, p.Output),
            ],
            PreferencePayload p =>
            [
                new(RecordFieldKind.Prompt, p.Prompt),
                new(RecordFieldKind.Chosen, p.Chosen),
                new(RecordFieldKind.Rejected, p.Rejected),
            ],
            ChatPayload p =>
            [
                .. p.SystemPrompt is null ? [] : new[] { new RecordField(RecordFieldKind.SystemPrompt, p.SystemPrompt) },
                .. p.Messages.Select((m, i) => new RecordField(RecordFieldKind.Message, m.Content, m.Role, i + 1)),
            ],
            _ => [],
        };
    }

    /// <param name="oldPayload">The earlier content; <c>null</c> if the record did not exist.</param>
    /// <param name="newPayload">The later content; <c>null</c> if the record no longer exists.</param>
    public static IReadOnlyList<FieldDifference> Compare(RecordPayload? oldPayload, RecordPayload? newPayload)
    {
        var oldFields = oldPayload is null ? [] : FieldsOf(oldPayload);
        var newFields = newPayload is null ? [] : FieldsOf(newPayload);
        if (oldPayload is null || newPayload is null || oldPayload.Type != newPayload.Type)
        {
            return [.. oldFields.Select(f => One(f, null)), .. newFields.Select(f => One(null, f))];
        }

        if (oldPayload is not ChatPayload)
        {
            return [.. oldFields.Zip(newFields, (o, n) => One(o, n))];
        }

        var result = new List<FieldDifference>();
        var oldSystem = oldFields.FirstOrDefault(f => f.Kind == RecordFieldKind.SystemPrompt);
        var newSystem = newFields.FirstOrDefault(f => f.Kind == RecordFieldKind.SystemPrompt);
        if (oldSystem is not null || newSystem is not null)
        {
            result.Add(One(oldSystem, newSystem));
        }

        var oldMessages = oldFields.Where(f => f.Kind == RecordFieldKind.Message).ToList();
        var newMessages = newFields.Where(f => f.Kind == RecordFieldKind.Message).ToList();
        var deleted = new List<RecordField>();
        var inserted = new List<RecordField>();

        void FlushChanges()
        {
            // Changed turns pair up in order; the rest were removed or added.
            for (var i = 0; i < Math.Max(deleted.Count, inserted.Count); i++)
            {
                result.Add(One(i < deleted.Count ? deleted[i] : null, i < inserted.Count ? inserted[i] : null));
            }

            deleted.Clear();
            inserted.Clear();
        }

        foreach (var step in SequenceDiff.Compute(oldMessages, newMessages, MessageComparer.Instance))
        {
            switch (step.Kind)
            {
                case DiffKind.Equal:
                    FlushChanges();
                    result.Add(One(oldMessages[step.OldIndex], newMessages[step.NewIndex]));
                    break;
                case DiffKind.Deleted:
                    deleted.Add(oldMessages[step.OldIndex]);
                    break;
                default:
                    inserted.Add(newMessages[step.NewIndex]);
                    break;
            }
        }

        FlushChanges();
        return result;
    }

    private static FieldDifference One(RecordField? oldField, RecordField? newField) =>
        new(oldField, newField, TextDiff.Compute(oldField?.Text, newField?.Text));

    // Same speaker and text; the turn number is where the message is, not what it is.
    private sealed class MessageComparer : IEqualityComparer<RecordField>
    {
        public static readonly MessageComparer Instance = new();

        public bool Equals(RecordField? x, RecordField? y) =>
            x is not null && y is not null && x.Role == y.Role && string.Equals(x.Text, y.Text, StringComparison.Ordinal);

        public int GetHashCode(RecordField obj) => HashCode.Combine(obj.Role, obj.Text);
    }
}
