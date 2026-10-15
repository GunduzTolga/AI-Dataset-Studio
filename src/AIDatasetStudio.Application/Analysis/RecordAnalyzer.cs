using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Analysis;

/// <summary>
/// Counts tokens and applies the single-record quality rules (SDD §7, §8.2). Pure: the same
/// payload, tokenizer and custom terms always give the same result.
/// </summary>
public sealed partial class RecordAnalyzer(ITokenCounter tokens, PiiScanner pii)
{
    private const int UnbalancedRatio = 50;
    private const int UnbalancedMinimumTokens = 200;
    private const int EchoMinimumLength = 40;

    // Answers that carry no information (SDD §8.2 "tamam", "evet", "bilmiyorum"), normalized.
    private static readonly HashSet<string> ThrowawayAnswers = new(StringComparer.Ordinal)
    {
        "tamam", "evet", "hayir", "bilmiyorum", "olur", "peki", "tesekkurler", "tesekkur ederim",
        "ok", "okay", "yes", "no", "idk", "i don t know", "thanks", "thank you", "sure", "n a", "none",
    };

    public RecordAnalysisResult Analyze(RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var issues = new List<QualityIssue>();
        void Issue(QualityRule rule, string? detail = null) =>
            issues.Add(new QualityIssue(rule, QualityRules.SeverityOf(rule), detail));

        TokenCounts counts;
        string question;
        string answer;
        string? questionKeySource;

        switch (payload)
        {
            case InstructionPayload p:
                if (IsBlank(p.Instruction))
                {
                    Issue(QualityRule.EmptyContent, "Instruction");
                }

                if (IsBlank(p.Output))
                {
                    Issue(QualityRule.EmptyContent, "Output");
                }

                question = IsBlank(p.Input) ? p.Instruction : p.Instruction + "\n" + p.Input;
                answer = p.Output;
                // The reasoning is trained with the answer, so it counts on the answer side.
                counts = new TokenCounts(Count(p.Instruction) + Count(p.Input), Count(p.Reasoning) + Count(p.Output));
                questionKeySource = QuestionText(p);
                break;

            case ChatPayload p:
                var turns = p.Messages;
                if (!turns.Any(m => m.Role == ChatRole.User && !IsBlank(m.Content)))
                {
                    Issue(QualityRule.EmptyContent, "User");
                }

                if (!turns.Any(m => m.Role == ChatRole.Assistant && !IsBlank(m.Content)))
                {
                    Issue(QualityRule.EmptyContent, "Assistant");
                }
                else if (turns.FirstOrDefault(m => IsBlank(m.Content)) is { } empty)
                {
                    Issue(QualityRule.EmptyContent, empty.Role.ToString());
                }

                question = turns.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
                answer = turns.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Content ?? string.Empty;
                counts = new TokenCounts(
                    Count(p.SystemPrompt) + turns.Where(m => m.Role != ChatRole.Assistant).Sum(m => Count(m.Content)),
                    turns.Where(m => m.Role == ChatRole.Assistant).Sum(m => Count(m.Reasoning) + Count(m.Content)));
                questionKeySource = QuestionText(p);
                break;

            case PreferencePayload p:
                if (IsBlank(p.Prompt))
                {
                    Issue(QualityRule.EmptyContent, "Prompt");
                }

                if (IsBlank(p.Chosen))
                {
                    Issue(QualityRule.EmptyContent, "Chosen");
                }

                if (IsBlank(p.Rejected))
                {
                    Issue(QualityRule.EmptyContent, "Rejected");
                }
                else if (!IsBlank(p.Chosen) && Normalize(p.Chosen) == Normalize(p.Rejected))
                {
                    Issue(QualityRule.IdenticalAnswers);
                }

                question = p.Prompt;
                answer = p.Chosen;
                counts = new TokenCounts(Count(p.Prompt), Math.Max(Count(p.Chosen), Count(p.Rejected)));
                // Several pairs per prompt are normal in DPO data, so the conflict rule does not apply.
                questionKeySource = null;
                break;

            case ToolCallingPayload p:
                if (!p.Messages.Any(m => m.Role == ChatRole.User && !IsBlank(m.Content)))
                {
                    Issue(QualityRule.EmptyContent, "User");
                }

                // The answer: the last turn, unless it ends the example with calls.
                var last = p.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
                if (last is null || (last.ToolCalls.Count == 0 && IsBlank(last.Content)))
                {
                    Issue(QualityRule.EmptyContent, "Assistant");
                }

                CheckTools(p, Issue);
                question = p.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
                answer = last is { ToolCalls.Count: 0 } ? last.Content : string.Empty;

                // The functions and their results are read by the model; the calls are written by it.
                var calls = p.Messages.SelectMany(m => m.ToolCalls).ToList();
                counts = new TokenCounts(
                    Count(p.SystemPrompt) + p.Tools.Sum(Count) + p.Messages.Where(m => m.Role != ChatRole.Assistant).Sum(m => Count(m.Content))
                        + calls.Sum(c => Count(c.Result)),
                    p.Messages.Where(m => m.Role == ChatRole.Assistant).Sum(m => Count(m.Reasoning) + Count(m.Content))
                        + calls.Sum(c => Count(c.Name) + Count(c.Arguments)));
                questionKeySource = QuestionText(p);
                break;

            default:
                return new RecordAnalysisResult(default, [], null);
        }

        // Punctuation-only answers ("-", "...") normalize to nothing.
        if (!IsBlank(answer) && Normalize(answer) is var normalized && (normalized.Length == 0 || ThrowawayAnswers.Contains(normalized)))
        {
            Issue(QualityRule.ShortAnswer);
        }

        var answerTokens = Count(answer);
        if (!IsBlank(question) && answerTokens >= UnbalancedMinimumTokens && answerTokens > UnbalancedRatio * Math.Max(1, Count(question)))
        {
            Issue(QualityRule.UnbalancedLength);
        }

        if (IsEcho(question, answer))
        {
            Issue(QualityRule.EchoedInput);
        }

        foreach (var (field, text) in Fields(payload))
        {
            if (CodeFenceRegex().Matches(text).Count % 2 == 1)
            {
                Issue(QualityRule.UnclosedCodeBlock, field);
            }

            if (HasInvalidJson(text))
            {
                Issue(QualityRule.InvalidJson, field);
            }
        }

        var kinds = pii.FindKinds(payload);
        if (kinds.Count > 0)
        {
            Issue(QualityRule.PersonalData, string.Join(',', kinds.Order()));
        }

        var normalizedQuestion = questionKeySource is null || IsBlank(questionKeySource) ? null : Normalize(questionKeySource);
        return new RecordAnalysisResult(
            counts,
            issues,
            normalizedQuestion is null ? null : Fingerprint(normalizedQuestion),
            normalizedQuestion is null ? null : NearDuplicates.Signature(normalizedQuestion));

        int Count(string? text) => string.IsNullOrEmpty(text) ? 0 : tokens.CountTokens(text);
    }

    /// <summary>
    /// The text that identifies "the same question" (and near-duplicate questions): instruction and
    /// input, or for a conversation its whole prompt side (system prompt and user turns). <c>null</c>
    /// for preference pairs, where several pairs per prompt are normal.
    /// </summary>
    public static string? QuestionText(RecordPayload payload) => payload switch
    {
        InstructionPayload p => IsBlank(p.Input) ? p.Instruction : p.Instruction + "\n" + p.Input,
        ChatPayload p => string.Join('\n', new[] { p.SystemPrompt ?? string.Empty }.Concat(p.Messages.Where(m => m.Role != ChatRole.Assistant).Select(m => m.Content))),
        ToolCallingPayload p => string.Join('\n', new[] { p.SystemPrompt ?? string.Empty }.Concat(p.Messages.Where(m => m.Role != ChatRole.Assistant).Select(m => m.Content))),
        _ => null,
    };

    // Tools name functions, calls name tools with object arguments that have what the function
    // requires, and the turns are in an order the formats can write (SDD §4.4).
    private static void CheckTools(ToolCallingPayload payload, Action<QualityRule, string?> report)
    {
        // A function called twice is named once.
        var reported = new HashSet<(QualityRule, string?)>();
        void Issue(QualityRule rule, string? detail)
        {
            if (reported.Add((rule, detail)))
            {
                report(rule, detail);
            }
        }

        var tools = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < payload.Tools.Count; i++)
        {
            if (ToolCalls.NameOf(payload.Tools[i]) is { } name)
            {
                tools.TryAdd(name, payload.Tools[i]);
            }
            else
            {
                Issue(QualityRule.InvalidTool, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        foreach (var call in payload.Messages.SelectMany(m => m.ToolCalls))
        {
            var known = tools.TryGetValue(call.Name, out var tool);
            if (!known)
            {
                Issue(QualityRule.UnknownTool, call.Name);
            }

            if (ToolCalls.Object(call.Arguments) is not { } arguments)
            {
                Issue(QualityRule.InvalidArguments, call.Name);
            }
            else if (known)
            {
                foreach (var required in ToolCalls.RequiredOf(tool!).Where(r => !arguments.TryGetProperty(r, out _)))
                {
                    Issue(QualityRule.MissingArgument, call.Name + "." + required);
                }
            }
        }

        if (!ToolCalls.IsInOrder(payload))
        {
            Issue(QualityRule.ToolTurnOrder, null);
        }
    }

    /// <summary>
    /// Lowercase, Turkish letters folded, punctuation dropped, whitespace collapsed: "Şifremi
    /// unuttum!" and "şifremi  UNUTTUM" compare equal.
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            var folded = c switch
            {
                'I' or 'İ' or 'ı' or 'i' => 'i',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ğ' or 'ğ' => 'g',
                'Ö' or 'ö' => 'o',
                'Ü' or 'ü' => 'u',
                _ => char.ToLowerInvariant(c),
            };
            if (char.IsLetterOrDigit(folded))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(folded);
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }

    // Labelled texts of the record, for rules that report the field.
    private static IEnumerable<(string Field, string Text)> Fields(RecordPayload payload) => payload switch
    {
        InstructionPayload p => [("Instruction", p.Instruction), ("Input", p.Input), .. Reasoning(p.Reasoning), ("Output", p.Output)],
        ChatPayload p =>
        [
            .. (p.SystemPrompt is null ? [] : new[] { ("SystemPrompt", p.SystemPrompt) }),
            .. p.Messages.SelectMany(m => Reasoning(m.Reasoning).Append((m.Role.ToString(), m.Content))),
        ],
        ToolCallingPayload p =>
        [
            .. (p.SystemPrompt is null ? [] : new[] { ("SystemPrompt", p.SystemPrompt) }),
            .. p.Messages.SelectMany(m => Reasoning(m.Reasoning).Append((m.Role.ToString(), m.Content))
                .Concat(m.ToolCalls.Where(c => c.Result is not null).Select(c => ("ToolResult", c.Result!)))),
        ],
        PreferencePayload p => [("Prompt", p.Prompt), ("Chosen", p.Chosen), ("Rejected", p.Rejected)],
        _ => [],
    };

    private static IEnumerable<(string Field, string Text)> Reasoning(string? reasoning) =>
        reasoning is null ? [] : [("Reasoning", reasoning)];

    private static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    private static bool IsEcho(string question, string answer)
    {
        if (IsBlank(question) || IsBlank(answer))
        {
            return false;
        }

        var q = Normalize(question);
        var a = Normalize(answer);
        return q.Length > 0 && (a == q || (q.Length >= EchoMinimumLength && a.Contains(q, StringComparison.Ordinal) && a.Length < q.Length * 3 / 2));
    }

    // Text that is evidently meant as JSON (a whole object/array, or a ```json block) but does not parse.
    private static bool HasInvalidJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']'))
            && !IsJson(trimmed))
        {
            return true;
        }

        return JsonBlockRegex().Matches(text).Any(m => !IsJson(m.Groups[1].Value));
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Fingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    [GeneratedRegex(@"^[ \t]*```", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"```json[ \t]*\r?\n([\s\S]*?)\r?\n[ \t]*```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonBlockRegex();
}
