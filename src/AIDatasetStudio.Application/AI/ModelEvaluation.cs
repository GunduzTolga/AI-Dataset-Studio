using System.Globalization;
using System.Text;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// One record as a test question: what the model is asked, as a model trained on the dataset
/// would see it, and the dataset's own answer to compare with.
/// </summary>
/// <param name="Question">The question as text, for the grader and the report.</param>
public sealed record EvaluationItem(IReadOnlyList<AIChatMessage> Messages, string Question, string Reference);

/// <summary>
/// A model's answer to a test question, its grade against the reference and the word overlap.
/// </summary>
/// <param name="Score">1–5 from the grader, or <c>null</c> when its verdict could not be read.</param>
/// <param name="F1">Word overlap with the reference, 0 to 1 (SQuAD-style token F1).</param>
public sealed record EvaluationResult(string Answer, int? Score, string Reason, double F1);

/// <summary>
/// What an evaluation came to.
/// </summary>
/// <param name="Scored">Answers the grader scored.</param>
/// <param name="Counts">How many answers got each score, 1 to 5.</param>
public sealed record EvaluationSummary(int Answered, int Scored, double? AverageScore, double? AverageF1, IReadOnlyList<int> Counts);

/// <summary>
/// Model evaluation (SDD §21 V4, M22): a model answers the dataset's questions, and a grader
/// model compares each answer with the dataset's answer. Questions go to the model as they are,
/// without the app's writing rules, so the score says how the model would do in use. A sample is
/// drawn by a seeded hash, so the same seed always asks the same questions.
/// </summary>
public static class ModelEvaluation
{
    /// <summary>
    /// Questions are asked at temperature 0: the same model gives the same answers in a re-run.
    /// </summary>
    public const double Temperature = 0;

    public const int DefaultSampleSize = 20;
    public const int MaxSampleSize = 500;

    private const string GradeRules =
        "You grade a model's answer against the reference answer of a test set. Rate how well the model's answer " +
        "matches the reference in meaning, from 1 to 5:\n" +
        "5 - says what the reference says: correct and complete; wording may differ.\n" +
        "4 - correct, with a small omission or an unneeded addition.\n" +
        "3 - partly correct: misses or gets wrong a part that matters.\n" +
        "2 - mostly wrong or off the question, with little of the reference.\n" +
        "1 - wrong, contradicting the reference, empty or unrelated.\n" +
        "- Judge the facts and the advice, not the style or the length. Extra correct detail is not an error.\n" +
        "Write the reason in {0}: one short sentence saying what differs, or that it matches.\n" +
        "Reply with only a JSON object, nothing before or after it: {{\"score\": 4, \"reason\": \"...\"}}";

    /// <summary>
    /// The dataset types whose records have a question and an answer to test with.
    /// </summary>
    public static bool Supports(DatasetType type) => type is DatasetType.Instruction or DatasetType.Chat or DatasetType.Preference;

    /// <summary>
    /// The record as a test question, or <c>null</c> when it has no question or no answer: an
    /// instruction's output, a conversation's last assistant turn after a user turn, or a
    /// preference pair's chosen answer.
    /// </summary>
    public static EvaluationItem? Item(RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        switch (payload)
        {
            case InstructionPayload p when Has(p.Instruction) && Has(p.Output):
                var question = Has(p.Input) ? $"{p.Instruction.Trim()}\n\n{p.Input.Trim()}" : p.Instruction.Trim();
                return new EvaluationItem([new(ChatRole.User, question)], question, p.Output.Trim());
            case PreferencePayload p when Has(p.Prompt) && Has(p.Chosen):
                return new EvaluationItem([new(ChatRole.User, p.Prompt.Trim())], p.Prompt.Trim(), p.Chosen.Trim());
            case ChatPayload p:
                var last = p.Messages.Count - 1;
                if (last < 1 || p.Messages[last].Role != ChatRole.Assistant || !Has(p.Messages[last].Content))
                {
                    return null;
                }

                var turns = p.Messages.Take(last).Where(m => m.Role != ChatRole.System && Has(m.Content)).ToList();
                if (turns.Count == 0 || turns[^1].Role != ChatRole.User)
                {
                    return null;
                }

                var messages = new List<AIChatMessage>();
                if (Has(p.SystemPrompt))
                {
                    messages.Add(new AIChatMessage(ChatRole.System, p.SystemPrompt!.Trim()));
                }

                messages.AddRange(turns.Select(m => new AIChatMessage(m.Role, m.Content.Trim())));
                return new EvaluationItem(messages, Transcript(turns), p.Messages[last].Content.Trim());
            default:
                return null;
        }
    }

    /// <summary>
    /// The messages that ask the grader to compare <paramref name="answer"/> with the reference.
    /// </summary>
    /// <param name="language">The language the reason is written in, e.g. "Turkish".</param>
    public static IReadOnlyList<AIChatMessage> GradePrompt(EvaluationItem item, string answer, string language)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var user = new StringBuilder()
            .Append("Question:\n").Append(item.Question)
            .Append("\n\nReference answer:\n").Append(item.Reference)
            .Append("\n\nModel's answer:\n").Append(answer.Trim().Length == 0 ? "(empty)" : answer.Trim());
        return
        [
            new(ChatRole.System, string.Format(CultureInfo.InvariantCulture, GradeRules, language)),
            new(ChatRole.User, user.ToString()),
        ];
    }

    /// <summary>
    /// The grade in the grader's answer, with the word overlap; the score is <c>null</c> when
    /// the verdict cannot be read.
    /// </summary>
    public static EvaluationResult Result(EvaluationItem item, string answer, string verdict)
    {
        ArgumentNullException.ThrowIfNull(item);
        var read = Judging.Read(verdict);
        return new EvaluationResult(answer.Trim(), read?.Score, read?.Reason ?? string.Empty, TokenF1(answer, item.Reference));
    }

    /// <summary>
    /// The share of words two texts have in common (token F1, as in SQuAD): letters and digits,
    /// lower-cased; 1 for two empty texts.
    /// </summary>
    public static double TokenF1(string answer, string reference)
    {
        var predicted = Tokens(answer);
        var expected = Tokens(reference);
        if (predicted.Count == 0 || expected.Count == 0)
        {
            return predicted.Count == expected.Count ? 1 : 0;
        }

        var remaining = expected.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var common = 0;
        foreach (var token in predicted)
        {
            if (remaining.TryGetValue(token, out var count) && count > 0)
            {
                remaining[token] = count - 1;
                common++;
            }
        }

        if (common == 0)
        {
            return 0;
        }

        var precision = (double)common / predicted.Count;
        var recall = (double)common / expected.Count;
        return 2 * precision * recall / (precision + recall);
    }

    /// <summary>
    /// The records to ask, by a seeded hash of their ids: the same seed draws the same sample,
    /// and records added later do not move the ones already drawn.
    /// </summary>
    public static IReadOnlyList<Guid> Sample(IEnumerable<Guid> recordIds, int size, int seed)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        return [.. recordIds.OrderBy(id => Hash(id, seed)).ThenBy(id => id).Take(Math.Max(0, size))];
    }

    public static EvaluationSummary Summarize(IReadOnlyCollection<EvaluationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var scores = results.Select(r => r.Score).OfType<int>().ToList();
        var counts = new int[Judging.MaxScore];
        foreach (var score in scores)
        {
            counts[score - 1]++;
        }

        return new EvaluationSummary(
            results.Count,
            scores.Count,
            scores.Count == 0 ? null : scores.Average(),
            results.Count == 0 ? null : results.Average(r => r.F1),
            counts);
    }

    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                tokens.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            tokens.Add(word.ToString());
        }

        return tokens;
    }

    // FNV-1a over the id and the seed: stable across runs and platforms, unlike string hashes.
    private static ulong Hash(Guid id, int seed)
    {
        var hash = 14695981039346656037UL;
        Span<byte> bytes = stackalloc byte[20];
        id.TryWriteBytes(bytes);
        BitConverter.TryWriteBytes(bytes[16..], seed);
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    private static string Transcript(IEnumerable<ChatMessage> turns) =>
        string.Join("\n\n", turns.Select(m => (m.Role == ChatRole.User ? "User: " : "Assistant: ") + m.Content.Trim()));

    private static bool Has(string? text) => !string.IsNullOrWhiteSpace(text);
}
