using System.Globalization;
using System.Text;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// What a variation changes: the question only, or the answer's wording too.
/// </summary>
public enum VariationMode
{
    /// <summary>The question is asked another way; the answer is kept as it is.</summary>
    Questions,

    /// <summary>The question is asked another way and the answer told in other words.</summary>
    QuestionsAndAnswers,
}

/// <param name="Count">How many variations to write per record, 1–<see cref="Variations.MaxCount"/>.</param>
/// <param name="Casual">Whether some questions should read like quick chat messages, with typos.</param>
public sealed record VariationOptions(int Count, VariationMode Mode, bool Casual);

/// <summary>
/// The parts of a record that variations rewrite.
/// </summary>
/// <param name="Question">What the user asks: the instruction, the last user turn or the prompt.</param>
/// <param name="Context">What comes with the question and stays as it is (an instruction's input).</param>
/// <param name="Answer">The answer to it: the output, the assistant's reply or the chosen answer; empty when there is none yet.</param>
public sealed record VariationSource(string Question, string? Context, string Answer);

/// <summary>
/// Synthetic variations of a record (SDD §17.6): the same need asked in other words, so a model
/// learns to answer however it is asked. Instructions to the model are in English, which models
/// follow best; the variations are asked in the record's language.
/// </summary>
public static class Variations
{
    public const int DefaultCount = 3;
    public const int MaxCount = 10;

    /// <summary>
    /// A variation this close to the question it varies adds little and is left out by default
    /// (the near-duplicate threshold, ADR-032).
    /// </summary>
    public const double TooSimilar = NearDuplicates.Threshold;

    private const string Rules =
        "You write variations of an example in a dataset that trains an assistant, so that it learns to answer " +
        "the same need however it is asked.\n" +
        "- Write {0} different ways a user could ask the question. Each must ask for exactly the same thing, so " +
        "that the same answer fits it: do not add, drop or change details, names or numbers.\n" +
        "- Vary the wording, the sentence structure and the length; do not just swap single words.\n";

    private const string CasualRule =
        "- Make some of them casual, the way people type in a chat, and some with common typos or without punctuation.\n";

    private const string AnswerRule =
        "- For each question, write the answer again in other words: the same facts and meaning, nothing new, about the same length.\n";

    private const string Ending =
        "- Write in the language of the example.\n" +
        "Reply with only a JSON array, nothing before or after it: {0}";

    /// <summary>
    /// The dataset types variations can be written for.
    /// </summary>
    public static bool Supports(DatasetType type) => type is DatasetType.Instruction or DatasetType.Chat or DatasetType.Preference;

    /// <summary>
    /// The question and answer of a record, or <c>null</c> when it asks nothing yet.
    /// </summary>
    public static VariationSource? SourceOf(RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var source = payload switch
        {
            InstructionPayload p => new VariationSource(p.Instruction.Trim(), string.IsNullOrWhiteSpace(p.Input) ? null : p.Input.Trim(), p.Output.Trim()),
            ChatPayload p when LastUserTurn(p) is { } user => new VariationSource(
                p.Messages[user].Content.Trim(),
                null,
                p.Messages.Skip(user + 1).FirstOrDefault(m => m.Role == ChatRole.Assistant)?.Content.Trim() ?? string.Empty),
            PreferencePayload p => new VariationSource(p.Prompt.Trim(), null, p.Chosen.Trim()),
            ToolCallingPayload p when LastUserTurn(p.Messages) is { } user => new VariationSource(p.Messages[user].Content.Trim(), null, ToolAnswer(p.Messages.Skip(user + 1))),
            _ => null,
        };
        return source is { Question.Length: > 0 } ? source : null;
    }

    public static IReadOnlyList<AIChatMessage> Prompt(VariationSource source, VariationOptions options, string? guidance)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        var answers = options.Mode == VariationMode.QuestionsAndAnswers && source.Answer.Length > 0;
        var system = new StringBuilder(string.Format(CultureInfo.InvariantCulture, Rules, Math.Clamp(options.Count, 1, MaxCount)));
        if (options.Casual)
        {
            system.Append(CasualRule);
        }

        if (answers)
        {
            system.Append(AnswerRule);
        }

        var format = answers ? """[{"question": "...", "answer": "..."}]""" : """[{"question": "..."}]""";
        system.Append(string.Format(CultureInfo.InvariantCulture, Ending, format));
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            system.Append("\n\nInstructions for this dataset:\n").Append(guidance.Trim());
        }

        var user = new StringBuilder("Question:\n").Append(source.Question);
        if (source.Context is { } context)
        {
            user.Append("\n\nContext that comes with the question (it stays as it is):\n").Append(context);
        }

        if (source.Answer.Length > 0)
        {
            user.Append("\n\nAnswer:\n").Append(source.Answer);
        }

        return [new(ChatRole.System, system.ToString()), new(ChatRole.User, user.ToString())];
    }

    /// <summary>
    /// The record a variation makes: the source with its question, and in
    /// <see cref="VariationMode.QuestionsAndAnswers"/> its answer, replaced. Everything else (an
    /// instruction's input, a chat's system prompt and earlier turns, a rejected answer) is kept,
    /// and so is the reasoning of an answer that is kept.
    /// </summary>
    public static RecordPayload Payload(RecordPayload source, QAPair variation, VariationMode mode)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(variation);
        var rewrite = mode == VariationMode.QuestionsAndAnswers && variation.Answer.Length > 0;
        switch (source)
        {
            case InstructionPayload p:
                return rewrite
                    ? new InstructionPayload(variation.Question, p.Input, variation.Answer)
                    : new InstructionPayload(variation.Question, p.Input, p.Output, p.Reasoning);
            case PreferencePayload p:
                return new PreferencePayload(variation.Question, rewrite ? variation.Answer : p.Chosen, p.Rejected);
            case ChatPayload p when LastUserTurn(p) is { } user:
                var messages = p.Messages.ToList();
                messages[user] = new ChatMessage(ChatRole.User, variation.Question);
                var reply = messages.FindIndex(user + 1, m => m.Role == ChatRole.Assistant);
                if (rewrite && reply >= 0)
                {
                    messages[reply] = new ChatMessage(ChatRole.Assistant, variation.Answer);
                }

                return new ChatPayload(p.SystemPrompt, messages);
            default:
                throw new ArgumentException($"Variations cannot be made of a {source.Type} record.", nameof(source));
        }
    }

    /// <summary>
    /// Whether two questions are the same once normalized (case, Turkish letters, punctuation, spacing).
    /// </summary>
    public static bool AreSame(string a, string b) =>
        string.Equals(RecordAnalyzer.Normalize(a), RecordAnalyzer.Normalize(b), StringComparison.Ordinal);

    /// <summary>
    /// How much a variation's question shares with the question it varies (0–1, as near duplicates are measured).
    /// </summary>
    public static double Similarity(string question, string variation) => NearDuplicates.Similarity(question, variation);

    private static int? LastUserTurn(ChatPayload payload) => LastUserTurn(payload.Messages);

    private static int? LastUserTurn(IReadOnlyList<ChatMessage> messages)
    {
        var index = messages.ToList().FindLastIndex(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content));
        return index >= 0 ? index : null;
    }

    // What answers a question in a tool calling record: the reply written from the results, or
    // the calls when the example ends with them.
    private static string ToolAnswer(IEnumerable<ChatMessage> after) => after.TakeWhile(m => m.Role == ChatRole.Assistant).LastOrDefault() switch
    {
        null => string.Empty,
        { ToolCalls.Count: > 0 } calling => string.Join("\n", calling.ToolCalls.Select(c => c.Name + " " + c.Arguments)),
        { } reply => reply.Content.Trim(),
    };
}
