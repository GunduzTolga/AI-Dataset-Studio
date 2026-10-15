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
        [TargetField.Input] = ["input", "girdi", "context", "baglam", "ekbilgi"],
        [TargetField.Output] = ["output", "cikti", "response", "answer", "cevap", "yanit", "completion", "assistant", "asistan", "target"],
        [TargetField.SystemPrompt] = ["systemprompt", "system", "sistem", "sistemtalimati", "systemmessage"],
        [TargetField.User] = ["user", "kullanici", "human", "question", "soru", "prompt", "istem", "instruction", "talimat", "input"],
        [TargetField.Assistant] = ["assistant", "asistan", "gpt", "answer", "cevap", "yanit", "response", "output", "cikti", "completion"],
        [TargetField.Prompt] = ["prompt", "istem", "question", "soru", "instruction", "talimat", "query"],
        [TargetField.Chosen] = ["chosen", "preferred", "tercihedilen", "secilen", "accepted"],
        [TargetField.Rejected] = ["rejected", "dispreferred", "nonpreferred", "reddedilen"],
        [TargetField.Reasoning] =
        [
            "reasoning", "reasoningcontent", "thinking", "think", "thought", "thoughts", "rationale", "cot", "chainofthought",
            "akilyurutme", "dusunme", "dusunce", "gerekce",
        ],
    };

    // Normalized parts of speaker names (see Normalize); "Müşteri Hizmetleri" is an agent.
    private static readonly string[] CustomerNames = ["musteri", "customer", "client", "kullanici", "user", "alici", "buyer", "ziyaretci", "visitor", "human"];

    private static readonly string[] AgentNames =
    [
        "temsilci", "destek", "agent", "support", "asistan", "assistant", "operator", "satici", "seller", "magaza", "store", "bot",
        "yetkili", "danisman", "advisor", "hizmet", "service", "helpdesk",
    ];

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
            new(TargetField.Reasoning, IsRequired: false),
        ],
        DatasetType.Chat =>
        [
            new(TargetField.SystemPrompt, IsRequired: false),
            new(TargetField.User, IsRequired: true),
            new(TargetField.Assistant, IsRequired: true),
            new(TargetField.Reasoning, IsRequired: false),
        ],
        DatasetType.Preference =>
        [
            new(TargetField.Prompt, IsRequired: true),
            new(TargetField.Chosen, IsRequired: true),
            new(TargetField.Rejected, IsRequired: true),
        ],
        _ => [],
    };

    /// <summary>
    /// The mappable fields for rows of <paramref name="layout"/>: for a conversation log, the
    /// customer (whose messages are user turns) and the agent (assistant turns; when unmapped,
    /// every other speaker).
    /// </summary>
    public static IReadOnlyList<TargetFieldInfo> FieldsFor(DatasetType type, SourceLayout layout) =>
        layout == SourceLayout.Transcript
            ? Supports(layout, type) ? [new(TargetField.User, IsRequired: true), new(TargetField.Assistant, IsRequired: false)] : []
            : FieldsFor(type);

    /// <summary>
    /// Whether the user maps columns (or a log's speakers) to fields.
    /// </summary>
    public static bool IsColumnBased(SourceLayout layout) =>
        layout is SourceLayout.Custom or SourceLayout.Alpaca or SourceLayout.PromptCompletion or SourceLayout.HuggingFaceDpo
            or SourceLayout.Sections or SourceLayout.Paragraphs or SourceLayout.Transcript;

    /// <summary>
    /// Whether rows of <paramref name="layout"/> can become records of <paramref name="type"/>.
    /// Column-based layouts work with every type (the user maps the columns).
    /// </summary>
    public static bool Supports(SourceLayout layout, DatasetType type) => layout switch
    {
        SourceLayout.Transcript => type is DatasetType.Chat or DatasetType.Instruction,
        _ when IsColumnBased(layout) => FieldsFor(type).Count > 0,
        // A conversation without calls is also a tool calling example: one that needs no tool.
        SourceLayout.OpenAiMessages or SourceLayout.ShareGpt => type is DatasetType.Chat or DatasetType.Instruction or DatasetType.ToolCalling,
        SourceLayout.OpenAiPreference => type is DatasetType.Preference,
        SourceLayout.OpenAiTools or SourceLayout.ShareGptTools or SourceLayout.XLam => type is DatasetType.ToolCalling,
        _ => false,
    };

    /// <summary>
    /// The dataset type a layout naturally belongs to, or <c>null</c> if it is not clear.
    /// </summary>
    public static DatasetType? NaturalType(SourceLayout layout) => layout switch
    {
        SourceLayout.Alpaca or SourceLayout.PromptCompletion => DatasetType.Instruction,
        SourceLayout.OpenAiMessages or SourceLayout.ShareGpt or SourceLayout.Transcript => DatasetType.Chat,
        SourceLayout.HuggingFaceDpo or SourceLayout.OpenAiPreference => DatasetType.Preference,
        SourceLayout.OpenAiTools or SourceLayout.ShareGptTools or SourceLayout.XLam => DatasetType.ToolCalling,
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
            return Most(e => HasTools(e, "messages")) ? SourceLayout.OpenAiTools : SourceLayout.OpenAiMessages;
        }

        if (Most(e => HasArray(e, "conversations")))
        {
            return Most(e => HasTools(e, "conversations")) ? SourceLayout.ShareGptTools : SourceLayout.ShareGpt;
        }

        if (Most(e => e.TryGetProperty("query", out _) && e.TryGetProperty("answers", out _)))
        {
            return SourceLayout.XLam;
        }

        return DetectFromColumns(ColumnsOf(objects));
    }

    // A conversation with function calls: tools offered, or a turn that calls or answers a call.
    private static bool HasTools(JsonElement row, string conversation) =>
        (row.TryGetProperty("tools", out var tools) && tools.ValueKind is not JsonValueKind.Null)
        || (row.TryGetProperty("functions", out var functions) && functions.ValueKind is not JsonValueKind.Null)
        || (row.TryGetProperty(conversation, out var turns) && turns.ValueKind == JsonValueKind.Array && turns.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.Object
            && (m.TryGetProperty("tool_calls", out _) || m.TryGetProperty("function_call", out _)
                || ((m.TryGetProperty("role", out var role) || m.TryGetProperty("from", out role)) && role.ValueKind == JsonValueKind.String
                    && Normalize(role.GetString()!) is "tool" or "function" or "functioncall" or "observation"))));

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
    /// Which speaker of a log is the customer and which the agent: by their names ("Müşteri",
    /// "Temsilci", "Customer", "Support"…), otherwise the one who writes first in most
    /// conversations is the customer and the other one the agent. With more speakers and no
    /// telling name, the agent stays unmapped: everyone but the customer answers.
    /// </summary>
    /// <param name="speakers">The speakers, most messages first.</param>
    /// <param name="openers">How many conversations each speaker opens.</param>
    public static IReadOnlyDictionary<TargetField, string> SuggestSpeakers(IReadOnlyList<string> speakers, IReadOnlyDictionary<string, int> openers)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        ArgumentNullException.ThrowIfNull(openers);
        var mapping = new Dictionary<TargetField, string>();
        bool IsAgent(string speaker) => AgentNames.Any(Normalize(speaker).Contains);
        var customer = speakers.FirstOrDefault(s => !IsAgent(s) && CustomerNames.Any(Normalize(s).Contains))
            ?? speakers.Where(s => !IsAgent(s)).OrderByDescending(s => openers.GetValueOrDefault(s)).FirstOrDefault();
        if (customer is null)
        {
            return mapping;
        }

        mapping[TargetField.User] = customer;
        var others = speakers.Where(s => s != customer).ToList();
        var agents = others.Where(IsAgent).ToList();
        var agent = agents.Count == 1 ? agents[0] : agents.Count == 0 && others.Count == 1 ? others[0] : null;
        if (agent is not null)
        {
            mapping[TargetField.Assistant] = agent;
        }

        return mapping;
    }

    /// <summary>
    /// For Markdown sections and paragraphs of text: the heading or first line asks, the text
    /// under it answers. Optional fields (input, system prompt) and the rejected answer stay
    /// unmapped.
    /// </summary>
    public static IReadOnlyDictionary<TargetField, string> SuggestTextMapping(SourceLayout layout, DatasetType type)
    {
        var (question, answer) = layout switch
        {
            SourceLayout.Sections => (TextColumns.Heading, TextColumns.Content),
            SourceLayout.Paragraphs => (TextColumns.FirstLine, TextColumns.Rest),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Not a text layout."),
        };
        var required = FieldsFor(type).Where(f => f.IsRequired).Select(f => f.Field).ToList();
        var mapping = new Dictionary<TargetField, string>();
        if (required.Count >= 2)
        {
            mapping[required[0]] = question;
            mapping[required[1]] = answer;
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
