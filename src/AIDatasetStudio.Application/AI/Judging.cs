using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// What a judgment found besides the score.
/// </summary>
[Flags]
public enum JudgeFlags
{
    None = 0,

    /// <summary>The answer says something its source passage does not support.</summary>
    Ungrounded = 1,

    /// <summary>In a preference pair, the rejected answer is the better one.</summary>
    RejectedBetter = 2,

    /// <summary>In a preference pair, neither answer is clearly better.</summary>
    NoPreference = 4,
}

/// <summary>
/// A model's judgment of a record as a training example.
/// </summary>
/// <param name="Score">1 (wrong, harmful or empty) to 5 (correct, complete, ready to train on).</param>
/// <param name="Reason">Why, in one or two sentences, in the language the model was asked for.</param>
public sealed record JudgeVerdict(int Score, string Reason, JudgeFlags Flags);

/// <summary>
/// Whether the answers of two records that ask the same thing contradict each other.
/// </summary>
public sealed record ConflictVerdict(bool Contradicts, string Reason);

/// <summary>
/// LLM-as-a-judge (SDD §17.7): a model rates each record from 1 to 5 with a reason, checks an
/// answer written from a document against its passage and the order of a preference pair, and
/// tells whether records that ask the same thing give contradicting answers. Instructions to the
/// model are in English, which models follow best; the reason is asked in the app's language.
/// </summary>
public static partial class Judging
{
    public const int MinScore = 1;
    public const int MaxScore = 5;

    /// <summary>
    /// Scores up to this one are low: the record needs work before training on it.
    /// </summary>
    public const int LowScore = 2;

    /// <summary>
    /// Judgments are asked at temperature 0, so the same record gets the same judgment.
    /// </summary>
    public const double Temperature = 0;

    private static readonly string[] ScoreKeys = ["score", "rating", "puan", "grade", "skor"];
    private static readonly string[] ReasonKeys = ["reason", "reasoning", "explanation", "rationale", "gerekçe", "gerekce", "comment", "açıklama"];
    private static readonly string[] GroundedKeys = ["grounded", "supported", "faithful"];
    private static readonly string[] BetterKeys = ["better", "preferred", "winner"];
    private static readonly string[] ContradictsKeys = ["contradicts", "contradiction", "contradictory", "conflict", "çelişki", "celiski"];

    private const string Scale =
        "You review examples in a dataset that trains an assistant. Rate {0} as a training example, from 1 to 5:\n" +
        "5 - correct, complete and helpful; does what was asked, as the instructions say; ready to train on.\n" +
        "4 - good, with small flaws such as wording or a missing detail.\n" +
        "3 - usable, but with clear problems: incomplete, vague, padded, or off in tone or format.\n" +
        "2 - mostly wrong or unhelpful, or not what was asked.\n" +
        "1 - wrong, harmful, unrelated or empty.\n" +
        "- Check the facts as well as you can. Do not reward length.\n";

    private const string ToolRule =
        "- The assistant may call the functions listed with the example. It should call the right function with correct " +
        "arguments when one is needed, call none when none is needed, and answer from the results; wrong calls lower the score.\n";

    private const string ReasoningRule =
        "- The answer comes with the reasoning written before it. Mistakes in the reasoning, and reasoning that does not lead " +
        "to the answer, lower the score.\n";

    private const string GroundingRule =
        "- The example was written from the source passage given with it. The answer must be supported by the passage: " +
        "set \"grounded\" to false when it states something the passage does not say, and lower the score.\n";

    private const string PreferenceRule =
        "- The example is a preference pair. Rate the chosen answer, and say in \"better\" which answer is better: " +
        "\"chosen\", \"rejected\", or \"same\" when neither is clearly better.\n";

    private const string Ending =
        "Write the reason in {0}: one or two short sentences saying what is wrong, or why the answer is good.\n" +
        "Reply with only a JSON object, nothing before or after it: {1}";

    private const string ConflictRules =
        "You check a dataset that trains an assistant for examples that contradict each other. The two examples below ask the " +
        "same or nearly the same question. Decide whether their answers contradict each other: they state different facts, " +
        "numbers, dates, rules or advice that cannot both be true. Different wording, detail, length or tone is not a " +
        "contradiction, and neither is one answer saying more than the other.\n" +
        "Write the reason in {0}, in one short sentence; when they contradict, name what differs.\n" +
        "Reply with only a JSON object, nothing before or after it: {{\"contradicts\": true, \"reason\": \"...\"}}";

    /// <summary>
    /// The dataset types whose records can be judged.
    /// </summary>
    public static bool Supports(DatasetType type) => type is DatasetType.Instruction or DatasetType.Chat or DatasetType.Preference or DatasetType.ToolCalling;

    /// <summary>
    /// Whether the record asks and answers something: an empty question or answer has nothing to judge.
    /// </summary>
    public static bool CanJudge(RecordPayload payload) => payload switch
    {
        InstructionPayload p => !string.IsNullOrWhiteSpace(p.Instruction) && !string.IsNullOrWhiteSpace(p.Output),
        ChatPayload p => p.Messages.Any(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content))
            && p.Messages.Any(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Content)),
        PreferencePayload p => !string.IsNullOrWhiteSpace(p.Prompt) && !string.IsNullOrWhiteSpace(p.Chosen),
        ToolCallingPayload p => p.Messages.Any(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content))
            && p.Messages.Any(m => m.Role == ChatRole.Assistant && (!string.IsNullOrWhiteSpace(m.Content) || m.ToolCalls.Count > 0)),
        _ => false,
    };

    /// <param name="passage">The document passage the record was written from, if any.</param>
    /// <param name="guidance">The dataset's instructions for the model.</param>
    /// <param name="language">The language of the reason, in English ("Turkish").</param>
    public static IReadOnlyList<AIChatMessage> Prompt(RecordPayload payload, PassageSource? passage, string? guidance, string language)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var preference = payload is PreferencePayload { Rejected.Length: > 0 };
        var subject = payload switch
        {
            ChatPayload => "the assistant's replies in the conversation (the last one counts most)",
            ToolCallingPayload => "the assistant's function calls and replies in the conversation (the last reply counts most)",
            PreferencePayload => "the chosen answer to the prompt",
            _ => "the answer to the question",
        };

        var system = new StringBuilder(string.Format(CultureInfo.InvariantCulture, Scale, subject));
        var format = new StringBuilder("{\"score\": 4, \"reason\": \"...\"");
        if (payload is ToolCallingPayload)
        {
            system.Append(ToolRule);
        }

        if (ReasoningText.Has(payload))
        {
            system.Append(ReasoningRule);
        }

        if (passage is not null)
        {
            system.Append(GroundingRule);
            format.Append(", \"grounded\": true");
        }

        if (preference)
        {
            system.Append(PreferenceRule);
            format.Append(", \"better\": \"chosen\"");
        }

        system.Append(string.Format(CultureInfo.InvariantCulture, Ending, language, format.Append('}')));
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            system.Append("\n\nInstructions for this dataset, which the answer should follow:\n").Append(guidance.Trim());
        }

        var user = new StringBuilder();
        if (passage is not null)
        {
            var place = passage.Section.Length > 0 ? $"{passage.File} · {passage.Section}" : passage.File;
            user.Append("Source passage (").Append(place).Append("):\n").Append(passage.Passage.Trim()).Append("\n\n");
        }

        switch (payload)
        {
            case InstructionPayload p:
                user.Append("Question:\n").Append(p.Instruction.Trim());
                if (!string.IsNullOrWhiteSpace(p.Input))
                {
                    user.Append("\n\nInput:\n").Append(p.Input.Trim());
                }

                if (p.Reasoning is { } reasoning)
                {
                    user.Append("\n\nReasoning before the answer:\n").Append(reasoning.Trim());
                }

                user.Append("\n\nAnswer:\n").Append(p.Output.Trim());
                break;
            case ChatPayload p:
                user.Append("Conversation:");
                if (p.SystemPrompt is { } prompt && prompt.Trim().Length > 0)
                {
                    user.Append("\n\nSystem: ").Append(prompt.Trim());
                }

                foreach (var message in p.Messages.Where(m => !string.IsNullOrWhiteSpace(m.Content)))
                {
                    if (message.Reasoning is { } thought)
                    {
                        user.Append("\n\nAssistant's reasoning: ").Append(thought.Trim());
                    }

                    user.Append("\n\n").Append(message.Role switch
                    {
                        ChatRole.User => "User",
                        ChatRole.Assistant => "Assistant",
                        _ => "System",
                    }).Append(": ").Append(message.Content.Trim());
                }

                break;
            case ToolCallingPayload p:
                user.Append("Functions:");
                foreach (var tool in p.Tools)
                {
                    user.Append("\n").Append(ToolCalls.Function(tool) is { } function ? ToolCalls.Write(function) : tool);
                }

                user.Append("\n\nConversation:");
                if (p.SystemPrompt is { } instructions && instructions.Trim().Length > 0)
                {
                    user.Append("\n\nSystem: ").Append(instructions.Trim());
                }

                foreach (var message in p.Messages)
                {
                    if (message.Reasoning is { } thought)
                    {
                        user.Append("\n\nAssistant's reasoning: ").Append(thought.Trim());
                    }

                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        user.Append("\n\n").Append(message.Role == ChatRole.User ? "User" : "Assistant").Append(": ").Append(message.Content.Trim());
                    }

                    foreach (var call in message.ToolCalls)
                    {
                        user.Append("\n\nAssistant calls: ").Append(call.Name).Append(' ').Append(ToolCalls.Minify(call.Arguments));
                        if (call.Result is { } result)
                        {
                            user.Append("\n\nResult of ").Append(call.Name).Append(": ").Append(result.Trim());
                        }
                    }
                }

                break;
            case PreferencePayload p:
                user.Append("Prompt:\n").Append(p.Prompt.Trim()).Append("\n\nChosen answer:\n").Append(p.Chosen.Trim());
                if (preference)
                {
                    user.Append("\n\nRejected answer:\n").Append(p.Rejected.Trim());
                }

                break;
            default:
                throw new ArgumentException($"{payload.Type} records cannot be judged.", nameof(payload));
        }

        return [new(ChatRole.System, system.ToString()), new(ChatRole.User, user.ToString())];
    }

    /// <summary>
    /// The messages that ask whether two records' answers contradict each other.
    /// </summary>
    public static IReadOnlyList<AIChatMessage> ConflictPrompt(VariationSource first, VariationSource second, string language)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var user = new StringBuilder();
        Example(user, "Example A", first);
        user.Append("\n\n");
        Example(user, "Example B", second);
        return
        [
            new(ChatRole.System, string.Format(CultureInfo.InvariantCulture, ConflictRules, language)),
            new(ChatRole.User, user.ToString()),
        ];

        static void Example(StringBuilder user, string title, VariationSource source)
        {
            user.Append(title).Append("\nQuestion:\n").Append(source.Question);
            if (source.Context is { } context)
            {
                user.Append("\nInput:\n").Append(context);
            }

            user.Append("\nAnswer:\n").Append(source.Answer);
        }
    }

    /// <summary>
    /// The verdict in a model's answer, or <c>null</c> when it has no score from 1 to 5. A JSON
    /// object is looked for anywhere in the answer, under the usual key names in English or
    /// Turkish; without one, "Score: 4" or "4/5" is read and the rest of the text is the reason.
    /// </summary>
    public static JudgeVerdict? Read(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        foreach (var element in JsonObjects.In(answer).Reverse())
        {
            if (Score(JsonObjects.Property(element, ScoreKeys)) is not { } score)
            {
                continue;
            }

            var flags = JudgeFlags.None;
            if (JsonObjects.Flag(element, GroundedKeys) == false)
            {
                flags |= JudgeFlags.Ungrounded;
            }

            flags |= JsonObjects.Text(element, BetterKeys)?.ToLowerInvariant() switch
            {
                "rejected" or "b" or "reddedilen" => JudgeFlags.RejectedBetter,
                "same" or "tie" or "equal" or "neither" or "both" or "aynı" => JudgeFlags.NoPreference,
                _ => JudgeFlags.None,
            };
            return new JudgeVerdict(score, JsonObjects.Text(element, ReasonKeys) ?? string.Empty, flags);
        }

        // No JSON: "Score: 4 — …", "**Puan:** 4/5", "4/5".
        if (ScoreLine().Match(answer) is { Success: true } line)
        {
            var reason = (answer[..line.Index] + answer[(line.Index + line.Length)..]).Trim().Trim('-', '–', '—', ':', '*', ' ', '\n').Trim();
            return new JudgeVerdict(int.Parse(line.Groups["score"].Value, CultureInfo.InvariantCulture), reason, JudgeFlags.None);
        }

        return null;
    }

    /// <summary>
    /// Whether a model said two answers contradict each other, or <c>null</c> when it cannot be read.
    /// </summary>
    public static ConflictVerdict? ReadConflict(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        foreach (var element in JsonObjects.In(answer).Reverse())
        {
            if (JsonObjects.Flag(element, ContradictsKeys) is { } contradicts)
            {
                return new ConflictVerdict(contradicts, JsonObjects.Text(element, ReasonKeys) ?? string.Empty);
            }
        }

        return null;
    }

    private static int? Score(JsonElement? value)
    {
        double? number = value switch
        {
            { ValueKind: JsonValueKind.Number } v when v.TryGetDouble(out var d) => d,
            { ValueKind: JsonValueKind.String } v when ScoreText().Match(v.GetString() ?? string.Empty) is { Success: true } m =>
                double.Parse(m.Groups["score"].Value.Replace(',', '.'), CultureInfo.InvariantCulture),
            _ => null,
        };
        if (number is not { } n)
        {
            return null;
        }

        var rounded = (int)Math.Round(n, MidpointRounding.AwayFromZero);
        return rounded is >= MinScore and <= MaxScore ? rounded : null;
    }

    // "4", "4.5", "4/5", " 4 out of 5"
    [GeneratedRegex(@"^\s*(?<score>\d+(?:[.,]\d+)?)\s*(?:/\s*5|out of 5)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ScoreText();

    // "Score: 4", "**Puan:** 4/5", "Rating = 3"; or "4/5" on its own.
    [GeneratedRegex(@"(?:(?:score|rating|puan|skor)\s*\**\s*[:=]\s*\**\s*(?<score>[1-5])(?!\d)(?:\s*/\s*5)?)|(?:(?<![\d/])(?<score>[1-5])\s*/\s*5(?!\d))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScoreLine();
}
