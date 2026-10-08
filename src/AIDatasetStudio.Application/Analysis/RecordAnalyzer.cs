using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                counts = new TokenCounts(Count(p.Instruction) + Count(p.Input), Count(p.Output));
                questionKeySource = question;
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
                    turns.Where(m => m.Role == ChatRole.Assistant).Sum(m => Count(m.Content)));
                // The whole prompt side identifies "the same question" in a conversation.
                questionKeySource = string.Join('\n', new[] { p.SystemPrompt ?? string.Empty }.Concat(turns.Where(m => m.Role != ChatRole.Assistant).Select(m => m.Content)));
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

        var key = questionKeySource is null || IsBlank(questionKeySource) ? null : Fingerprint(Normalize(questionKeySource));
        return new RecordAnalysisResult(counts, issues, key);

        int Count(string? text) => string.IsNullOrEmpty(text) ? 0 : tokens.CountTokens(text);
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
        InstructionPayload p => [("Instruction", p.Instruction), ("Input", p.Input), ("Output", p.Output)],
        ChatPayload p => [.. (p.SystemPrompt is null ? [] : new[] { ("SystemPrompt", p.SystemPrompt) }), .. p.Messages.Select(m => (m.Role.ToString(), m.Content))],
        PreferencePayload p => [("Prompt", p.Prompt), ("Chosen", p.Chosen), ("Rejected", p.Rejected)],
        _ => [],
    };

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
