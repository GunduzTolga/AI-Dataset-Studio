using System.Text;
using System.Text.Json;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Import;

/// <summary>
/// Which payload fields each dataset type has, which layouts convert to which types, and how
/// layouts and column mappings are guessed from a file.
/// </summary>
public static class ImportLayouts
{
    private static readonly Dictionary<TargetField, string[]> Synonyms = new()
    {
        // Normalized names (lowercase, Turkish letters folded, punctuation removed), best first.
        [TargetField.Instruction] = ["instruction", "talimat", "prompt", "question", "soru", "query", "istem", "komut", "user", "kullanici"],
        [TargetField.Input] = ["input", "girdi", "context", "baglam"],
        [TargetField.Output] = ["output", "cikti", "response", "answer", "cevap", "yanit", "completion", "assistant", "asistan", "target"],
        [TargetField.SystemPrompt] = ["systemprompt", "system", "sistem", "sistemtalimati", "systemmessage"],
        [TargetField.User] = ["user", "kullanici", "human", "question", "soru", "prompt", "istem", "instruction", "talimat", "input"],
        [TargetField.Assistant] = ["assistant", "asistan", "gpt", "answer", "cevap", "yanit", "response", "output", "cikti", "completion"],
        [TargetField.Prompt] = ["prompt", "istem", "question", "soru", "instruction", "talimat", "query"],
        [TargetField.Chosen] = ["chosen", "preferred", "tercihedilen", "secilen", "accepted"],
        [TargetField.Rejected] = ["rejected", "dispreferred", "nonpreferred", "reddedilen"],
    };

    /// <summary>
    /// The mappable fields of a dataset type, in display order.
    /// </summary>
    public static IReadOnlyList<TargetFieldInfo> FieldsFor(DatasetType type) => type switch
    {
        DatasetType.Instruction =>
        [
            new(TargetField.Instruction, IsRequired: true),
            new(TargetField.Input, IsRequired: false),
            new(TargetField.Output, IsRequired: true),
        ],
        DatasetType.Chat =>
        [
            new(TargetField.SystemPrompt, IsRequired: false),
            new(TargetField.User, IsRequired: true),
            new(TargetField.Assistant, IsRequired: true),
        ],
        DatasetType.Preference =>
        [
            new(TargetField.Prompt, IsRequired: true),
            new(TargetField.Chosen, IsRequired: true),
            new(TargetField.Rejected, IsRequired: true),
        ],
        _ => [],
    };

    public static bool IsColumnBased(SourceLayout layout) =>
        layout is SourceLayout.Custom or SourceLayout.Alpaca or SourceLayout.PromptCompletion or SourceLayout.HuggingFaceDpo;

    /// <summary>
    /// Whether rows of <paramref name="layout"/> can become records of <paramref name="type"/>.
    /// Column-based layouts work with every type (the user maps the columns).
    /// </summary>
    public static bool Supports(SourceLayout layout, DatasetType type) => layout switch
    {
        _ when IsColumnBased(layout) => FieldsFor(type).Count > 0,
        SourceLayout.OpenAiMessages or SourceLayout.ShareGpt => type is DatasetType.Chat or DatasetType.Instruction,
        SourceLayout.OpenAiPreference => type is DatasetType.Preference,
        _ => false,
    };

    /// <summary>
    /// The dataset type a layout naturally belongs to, or <c>null</c> if it is not clear.
    /// </summary>
    public static DatasetType? NaturalType(SourceLayout layout) => layout switch
    {
        SourceLayout.Alpaca or SourceLayout.PromptCompletion => DatasetType.Instruction,
        SourceLayout.OpenAiMessages or SourceLayout.ShareGpt => DatasetType.Chat,
        SourceLayout.HuggingFaceDpo or SourceLayout.OpenAiPreference => DatasetType.Preference,
        _ => null,
    };

    /// <summary>
    /// Guesses the layout from the first rows of a JSON file.
    /// </summary>
    public static SourceLayout DetectFromJson(IReadOnlyList<JsonElement> sample)
    {
        var objects = sample.Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (objects.Count == 0)
        {
            return SourceLayout.Custom;
        }

        bool Most(Func<JsonElement, bool> predicate) => objects.Count(predicate) * 2 > objects.Count;
        bool HasArray(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;

        if (Most(e => e.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                      && HasArray(input, "messages") && e.TryGetProperty("preferred_output", out _)))
        {
            return SourceLayout.OpenAiPreference;
        }

        if (Most(e => HasArray(e, "messages")))
        {
            return SourceLayout.OpenAiMessages;
        }

        if (Most(e => HasArray(e, "conversations")))
        {
            return SourceLayout.ShareGpt;
        }

        return DetectFromColumns(ColumnsOf(objects));
    }

    /// <summary>
    /// Guesses a column-based layout from column names (CSV headers or JSON keys).
    /// </summary>
    public static SourceLayout DetectFromColumns(IReadOnlyList<string> columns)
    {
        var names = columns.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        if (names.Contains("chosen") && names.Contains("rejected"))
        {
            return SourceLayout.HuggingFaceDpo;
        }

        if (names.Contains("instruction") && names.Contains("output"))
        {
            return SourceLayout.Alpaca;
        }

        if (names.Contains("prompt") && names.Contains("completion"))
        {
            return SourceLayout.PromptCompletion;
        }

        return SourceLayout.Custom;
    }

    /// <summary>
    /// Top-level keys of JSON objects in order of first appearance.
    /// </summary>
    public static IReadOnlyList<string> ColumnsOf(IEnumerable<JsonElement> objects)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in objects.Where(e => e.ValueKind == JsonValueKind.Object))
        {
            foreach (var property in element.EnumerateObject())
            {
                if (seen.Add(property.Name))
                {
                    columns.Add(property.Name);
                }
            }
        }

        return columns;
    }

    /// <summary>
    /// Suggests which column feeds each field of <paramref name="type"/>, by column name
    /// (English and Turkish synonyms). Each column is used at most once.
    /// </summary>
    public static IReadOnlyDictionary<TargetField, string> SuggestMapping(IReadOnlyList<string> columns, DatasetType type)
    {
        var normalized = columns.Select(c => (Column: c, Name: Normalize(c))).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var mapping = new Dictionary<TargetField, string>();

        foreach (var field in FieldsFor(type))
        {
            foreach (var synonym in Synonyms[field.Field])
            {
                var match = normalized.FirstOrDefault(c => c.Name == synonym && !used.Contains(c.Column));
                if (match.Column is not null)
                {
                    mapping[field.Field] = match.Column;
                    used.Add(match.Column);
                    break;
                }
            }
        }

        return mapping;
    }

    /// <summary>
    /// Lowercases, folds Turkish letters to ASCII and drops everything but letters and digits,
    /// so "System Prompt", "system_prompt" and "Sistem Talimatı" compare predictably.
    /// </summary>
    public static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
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
            if (char.IsAsciiLetterOrDigit(folded))
            {
                builder.Append(folded);
            }
        }

        return builder.ToString();
    }
}
