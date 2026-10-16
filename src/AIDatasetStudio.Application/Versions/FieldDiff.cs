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

    /// <summary>The reasoning of the output, or of the assistant turn <see cref="RecordField.Turn"/> (ADR-052).</summary>
    Reasoning,

    /// <summary>A tool's definition; <see cref="RecordField.Turn"/> is its number (SDD §4.4).</summary>
    Tool,

    /// <summary>A call of the assistant turn <see cref="RecordField.Turn"/>: "name {arguments}".</summary>
    ToolCall,

    /// <summary>What a call of the assistant turn <see cref="RecordField.Turn"/> returned.</summary>
    ToolResult,
}

/// <summary>
/// One text of a record, in the order the editor shows them.
/// </summary>
/// <param name="Role">The speaker of a <see cref="RecordFieldKind.Message"/> (or of the turn whose reasoning it is).</param>
/// <param name="Turn">1-based position of a message (or of the turn whose reasoning it is) in the conversation; 0 for other fields.</param>
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
                .. p.Reasoning is null ? [] : new[] { new RecordField(RecordFieldKind.Reasoning, p.Reasoning) },
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
                .. p.Messages.SelectMany((m, i) => Turn(m, i + 1)),
            ],
            ToolCallingPayload p =>
            [
                .. p.SystemPrompt is null ? [] : new[] { new RecordField(RecordFieldKind.SystemPrompt, p.SystemPrompt) },
                .. p.Tools.Select((t, i) => new RecordField(RecordFieldKind.Tool, t, Turn: i + 1)),
                .. p.Messages.SelectMany((m, i) => Turn(m, i + 1)),
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

        if (oldPayload is not (ChatPayload or ToolCallingPayload))
        {
            // The same fields on both sides, but for the reasoning, which either side may lack.
            return [.. Order.Where(kind => oldFields.Any(f => f.Kind == kind) || newFields.Any(f => f.Kind == kind))
                .Select(kind => One(oldFields.FirstOrDefault(f => f.Kind == kind), newFields.FirstOrDefault(f => f.Kind == kind)))];
        }

        var result = new List<FieldDifference>();
        var oldSystem = oldFields.FirstOrDefault(f => f.Kind == RecordFieldKind.SystemPrompt);
        var newSystem = newFields.FirstOrDefault(f => f.Kind == RecordFieldKind.SystemPrompt);
        if (oldSystem is not null || newSystem is not null)
        {
            result.Add(One(oldSystem, newSystem));
        }

        // Tools pair up by number.
        var oldTools = oldFields.Where(f => f.Kind == RecordFieldKind.Tool).ToList();
        var newTools = newFields.Where(f => f.Kind == RecordFieldKind.Tool).ToList();
        for (var i = 0; i < Math.Max(oldTools.Count, newTools.Count); i++)
        {
            result.Add(One(i < oldTools.Count ? oldTools[i] : null, i < newTools.Count ? newTools[i] : null));
        }

        // Turns with their reasoning, calls and results, matched as one sequence.
        var oldMessages = oldFields.Where(f => f.Kind is not (RecordFieldKind.SystemPrompt or RecordFieldKind.Tool)).ToList();
        var newMessages = newFields.Where(f => f.Kind is not (RecordFieldKind.SystemPrompt or RecordFieldKind.Tool)).ToList();
        var deleted = new List<RecordField>();
        var inserted = new List<RecordField>();

        void FlushChanges()
        {
            // Changed turns (and reasoning) pair up in order; the rest were removed or added.
            foreach (var old in deleted)
            {
                var match = inserted.FindIndex(f => f.Kind == old.Kind);
                result.Add(One(old, match < 0 ? null : inserted[match]));
                if (match >= 0)
                {
                    inserted.RemoveAt(match);
                }
            }

            result.AddRange(inserted.Select(f => One(null, f)));
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

    // The order of the fields of single-turn records, as the editor shows them.
    private static readonly RecordFieldKind[] Order =
    [
        RecordFieldKind.Instruction, RecordFieldKind.Input, RecordFieldKind.Reasoning, RecordFieldKind.Output,
        RecordFieldKind.Prompt, RecordFieldKind.Chosen, RecordFieldKind.Rejected,
    ];

    // A turn, after its reasoning if it has any, and before its calls and their results.
    private static IEnumerable<RecordField> Turn(ChatMessage message, int turn)
    {
        if (message.Reasoning is { } reasoning)
        {
            yield return new RecordField(RecordFieldKind.Reasoning, reasoning, message.Role, turn);
        }

        yield return new RecordField(RecordFieldKind.Message, message.Content, message.Role, turn);
        foreach (var call in message.ToolCalls)
        {
            yield return new RecordField(RecordFieldKind.ToolCall, call.Name + " " + call.Arguments, message.Role, turn);
            if (call.Result is { } result)
            {
                yield return new RecordField(RecordFieldKind.ToolResult, result, message.Role, turn);
            }
        }
    }

    private static FieldDifference One(RecordField? oldField, RecordField? newField) =>
        new(oldField, newField, TextDiff.Compute(oldField?.Text, newField?.Text));

    // Same kind, speaker and text; the turn number is where the message is, not what it is.
    private sealed class MessageComparer : IEqualityComparer<RecordField>
    {
        public static readonly MessageComparer Instance = new();

        public bool Equals(RecordField? x, RecordField? y) =>
            x is not null && y is not null && x.Kind == y.Kind && x.Role == y.Role && string.Equals(x.Text, y.Text, StringComparison.Ordinal);

        public int GetHashCode(RecordField obj) => HashCode.Combine(obj.Kind, obj.Role, obj.Text);
    }
}
