using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Records;

/// <summary>
/// A record's reasoning (SDD §4.1, §4.2, ADR-052) in the text of its answer, as reasoning models
/// write it and the tools that train them read it: "&lt;think&gt;\n…\n&lt;/think&gt;\n\n" before the answer.
/// </summary>
public static class ReasoningText
{
    public const string Open = "<think>";
    public const string Close = "</think>";

    /// <summary>
    /// The answer with its reasoning in front, in the shape LLaMA-Factory's reasoning templates and
    /// Qwen3's chat template write; the answer alone when there is no reasoning.
    /// </summary>
    public static string Inline(string? reasoning, string answer) =>
        reasoning is null ? answer : $"{Open}\n{reasoning.Trim()}\n{Close}\n\n{answer}";

    /// <summary>
    /// Splits the "&lt;think&gt;…&lt;/think&gt;" block a text starts with off the answer after it.
    /// A text that does not start with a closed block is all answer.
    /// </summary>
    public static (string? Reasoning, string Answer) Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.Length - text.AsSpan().TrimStart().Length;
        if (string.CompareOrdinal(text, start, Open, 0, Open.Length) != 0 || text.IndexOf(Close, start, StringComparison.Ordinal) is not (>= 0 and var close))
        {
            return (null, text);
        }

        var reasoning = text[(start + Open.Length)..close].Trim();
        return (reasoning.Length == 0 ? null : reasoning, text[(close + Close.Length)..].TrimStart());
    }

    /// <summary>
    /// Whether any answer of the record has reasoning.
    /// </summary>
    public static bool Has(RecordPayload payload) => payload switch
    {
        InstructionPayload p => p.Reasoning is not null,
        ChatPayload p => p.Messages.Any(m => m.Reasoning is not null),
        _ => false,
    };

    /// <summary>
    /// The record without its reasoning, to train a model that answers without thinking first.
    /// </summary>
    public static RecordPayload Without(RecordPayload payload) => payload switch
    {
        InstructionPayload { Reasoning: not null } p => new InstructionPayload(p.Instruction, p.Input, p.Output),
        ChatPayload p when Has(p) => new ChatPayload(p.SystemPrompt, [.. p.Messages.Select(m => new ChatMessage(m.Role, m.Content))]),
        _ => payload,
    };
}
