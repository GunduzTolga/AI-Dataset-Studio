using System.Text;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// What a model is asked to write into a record.
/// </summary>
public enum AIWriteTarget
{
    /// <summary>The output of an instruction record.</summary>
    Answer,

    /// <summary>The assistant's turn after the last user turn of a conversation.</summary>
    Reply,

    /// <summary>The preferred answer of a preference pair.</summary>
    Chosen,

    /// <summary>A plausible but worse answer for a preference pair.</summary>
    Rejected,
}

/// <summary>
/// The conversations sent to a model to write part of a record (SDD §17). Instructions to the
/// model are in English, which models follow best; the answer is asked in the record's language.
/// </summary>
public static class AIPrompts
{
    public const int MaxGuidanceLength = 4_000;

    private const string Rules =
        "You write answers for a dataset that trains a language model. Reply with the answer only: " +
        "no preamble, no notes about yourself or the task. Answer in the language of the question.";

    private const string RejectedRules =
        "You write the rejected answer of a preference (DPO) training pair. Write an answer that looks " +
        "plausible but is clearly worse than a good one: vague, partly wrong, unhelpful, or ignoring part " +
        "of the question. Never say that it is worse or that it was written on purpose. Reply with the " +
        "answer only, in the language of the question.";

    /// <summary>
    /// The messages for <paramref name="target"/>, or <c>null</c> when the record has nothing to
    /// answer yet (an empty question, or a conversation without a user turn).
    /// </summary>
    /// <param name="guidance">The dataset's instructions for the model (tone, length, language…).</param>
    public static IReadOnlyList<AIChatMessage>? For(RecordPayload payload, AIWriteTarget target, string? guidance)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return (payload, target) switch
        {
            (InstructionPayload p, AIWriteTarget.Answer) => Single(Rules, guidance, Question(p.Instruction, p.Input)),
            (ChatPayload p, AIWriteTarget.Reply) => Conversation(p, guidance),
            (PreferencePayload p, AIWriteTarget.Chosen) => Single(Rules, guidance, Question(p.Prompt, null)),
            (PreferencePayload p, AIWriteTarget.Rejected) => Rejected(p, guidance),
            _ => throw new ArgumentException($"{target} cannot be written into a {payload.Type} record.", nameof(target)),
        };
    }

    /// <summary>
    /// The targets a dataset type offers, the main one first.
    /// </summary>
    public static IReadOnlyList<AIWriteTarget> TargetsFor(DatasetType type) => type switch
    {
        DatasetType.Instruction => [AIWriteTarget.Answer],
        DatasetType.Chat => [AIWriteTarget.Reply],
        DatasetType.Preference => [AIWriteTarget.Chosen, AIWriteTarget.Rejected],
        _ => [],
    };

    /// <summary>
    /// For a conversation: the turns the reply follows. A last assistant turn is written again.
    /// </summary>
    public static int ReplyIndex(ChatPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var messages = payload.Messages;
        return messages.Count > 0 && messages[^1].Role == ChatRole.Assistant ? messages.Count - 1 : messages.Count;
    }

    private static string? Question(string question, string? input)
    {
        if (string.IsNullOrWhiteSpace(question) && string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(input) ? question.Trim() : $"{question.Trim()}\n\n{input.Trim()}";
    }

    private static List<AIChatMessage>? Single(string rules, string? guidance, string? question) =>
        question is null ? null : [new(ChatRole.System, System(rules, null, guidance)), new(ChatRole.User, question)];

    private static List<AIChatMessage>? Conversation(ChatPayload payload, string? guidance)
    {
        var turns = payload.Messages.Take(ReplyIndex(payload)).Where(m => m.Role != ChatRole.System).ToList();
        if (!turns.Any(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content)))
        {
            return null;
        }

        var messages = new List<AIChatMessage> { new(ChatRole.System, System(Rules, payload.SystemPrompt, guidance)) };
        messages.AddRange(turns.Select(m => new AIChatMessage(m.Role, m.Content)));
        return messages;
    }

    private static List<AIChatMessage>? Rejected(PreferencePayload payload, string? guidance)
    {
        if (Question(payload.Prompt, null) is not { } question)
        {
            return null;
        }

        var user = string.IsNullOrWhiteSpace(payload.Chosen)
            ? question
            : $"Question:\n{question}\n\nA good answer, for reference:\n{payload.Chosen.Trim()}\n\nWrite the worse answer to the question.";
        return [new(ChatRole.System, System(RejectedRules, null, guidance)), new(ChatRole.User, user)];
    }

    // The record's own system prompt comes first: it is what the trained model will see.
    private static string System(string rules, string? recordSystemPrompt, string? guidance)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(recordSystemPrompt))
        {
            builder.Append(recordSystemPrompt.Trim()).Append("\n\n");
        }

        builder.Append(rules);
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            builder.Append("\n\nInstructions for this dataset:\n").Append(guidance.Trim());
        }

        return builder.ToString();
    }
}
