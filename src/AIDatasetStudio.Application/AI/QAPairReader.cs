using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// Reads question-answer pairs out of a model's answer while it is written, so each pair can be
/// shown as soon as it is complete. Models are asked for a JSON array but do not always keep to
/// it; this reader takes every JSON object with a question and an answer wherever it is (in a code
/// fence, after a sentence, inside <c>{"pairs": […]}</c>), under the usual key names in English
/// or Turkish. An answer without any JSON is read as "Q: … A: …" / "Soru: … Cevap: …" lines.
/// </summary>
/// <param name="questionsOnly">
/// Whether questions without an answer are taken too (synthetic variations, which may only ask
/// again): then objects with only a question count, and an answer without any JSON is also read
/// as a JSON array of strings or as a numbered or bulleted list.
/// </param>
public sealed partial class QAPairReader(bool questionsOnly = false)
{
    private static readonly string[] QuestionKeys = ["question", "q", "soru", "instruction", "prompt", "user"];
    private static readonly string[] AnswerKeys = ["answer", "a", "cevap", "yanıt", "yanit", "response", "output", "assistant"];

    private readonly StringBuilder _text = new();
    private readonly Stack<int> _objects = new();
    private readonly HashSet<string> _questions = new(StringComparer.OrdinalIgnoreCase);
    private int _scanned;
    private bool _inString;
    private bool _escaped;
    private bool _sawObject;

    /// <summary>
    /// The pairs read so far.
    /// </summary>
    public List<QAPair> Pairs { get; } = [];

    /// <summary>
    /// The whole answer so far.
    /// </summary>
    public string Text => _text.ToString();

    /// <summary>
    /// Adds a piece of the answer.
    /// </summary>
    /// <returns>The pairs the piece completed.</returns>
    public IReadOnlyList<QAPair> Push(string piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        _text.Append(piece);
        var found = new List<QAPair>();
        for (; _scanned < _text.Length; _scanned++)
        {
            var c = _text[_scanned];
            if (_inString)
            {
                if (_escaped)
                {
                    _escaped = false;
                }
                else if (c == '\\')
                {
                    _escaped = true;
                }
                else if (c == '"')
                {
                    _inString = false;
                }

                continue;
            }

            switch (c)
            {
                // Quotes count only inside an object: prose around the JSON may have its own.
                case '"' when _objects.Count > 0:
                    _inString = true;
                    break;
                case '{':
                    _objects.Push(_scanned);
                    _sawObject = true;
                    break;
                case '}' when _objects.Count > 0:
                    var start = _objects.Pop();
                    if (Read(_text.ToString(start, _scanned - start + 1), questionsOnly) is { } pair && Add(pair))
                    {
                        found.Add(pair);
                    }

                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// Ends the answer. When it had no JSON objects at all, its "Q: … A: …" lines are read (and,
    /// for questions only, a JSON array of strings or a list).
    /// </summary>
    /// <returns>The pairs found only now.</returns>
    public IReadOnlyList<QAPair> Complete()
    {
        if (_sawObject)
        {
            return [];
        }

        var found = Lines();
        if (found.Count == 0 && questionsOnly)
        {
            foreach (var question in Strings() ?? ListItems())
            {
                if (Add(new QAPair(question, string.Empty)))
                {
                    found.Add(Pairs[^1]);
                }
            }
        }

        return found;
    }

    private List<QAPair> Lines()
    {
        var found = new List<QAPair>();
        string? question = null;
        var answer = new StringBuilder();
        void Finish()
        {
            if (question is not null && answer.Length > 0 && Add(new QAPair(question, answer.ToString().Trim())))
            {
                found.Add(Pairs[^1]);
            }

            question = null;
            answer.Clear();
        }

        foreach (var line in _text.ToString().Split('\n'))
        {
            if (QuestionLine().Match(line) is { Success: true } q)
            {
                Finish();
                question = Clean(q.Groups["text"].Value);
            }
            else if (question is not null && answer.Length == 0 && AnswerLine().Match(line) is { Success: true } a)
            {
                answer.Append(Clean(a.Groups["text"].Value));
            }
            else if (question is not null && answer.Length > 0 && !string.IsNullOrWhiteSpace(line))
            {
                answer.Append('\n').Append(line.Trim());
            }
        }

        Finish();
        return found;
    }

    // ["Kargo ne kadar?", "Kargo ücreti nedir?"], in a code fence or not.
    private List<string>? Strings()
    {
        var text = _text.ToString();
        var start = text.IndexOf('[', StringComparison.Ordinal);
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text.AsMemory(start, end - start + 1), new JsonDocumentOptions { AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var strings = document.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            return strings.Count > 0 ? strings : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // "1. …", "2) …", "- …", "* …", "• …"
    private IEnumerable<string> ListItems() =>
        _text.ToString().Split('\n')
            .Select(line => ListItem().Match(line))
            .Where(m => m.Success)
            .Select(m => Clean(m.Groups["text"].Value).Trim('"', '“', '”'))
            .Where(text => text.Length > 0);

    private bool Add(QAPair pair)
    {
        if (!_questions.Add(pair.Question))
        {
            return false;
        }

        Pairs.Add(pair);
        return true;
    }

    private static QAPair? Read(string json, bool questionsOnly)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var question = Value(document.RootElement, QuestionKeys);
            var answer = Value(document.RootElement, AnswerKeys);
            return question is not { Length: > 0 } ? null
                : answer is { Length: > 0 } ? new QAPair(question, answer)
                : questionsOnly ? new QAPair(question, string.Empty)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Value(JsonElement element, string[] keys)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (keys.Any(k => string.Equals(k, property.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()?.Trim(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static string Clean(string text) => text.Trim().Trim('*', '_').Trim();

    // "Q: …", "1. Question: …", "**Soru 2:** …", "S: …"
    [GeneratedRegex(@"^\s*(?:\d+[.)]\s*)?[*_]*(?:q|question|soru|s)\s*\d*\s*[:.)\-–][*_]*\s*(?<text>\S.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionLine();

    // "A: …", "Answer: …", "**Cevap:** …", "Yanıt: …", "C: …"
    [GeneratedRegex(@"^\s*[*_]*(?:a|answer|cevap|yanıt|yanit|c|y)\s*\d*\s*[:.)\-–][*_]*\s*(?<text>\S.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnswerLine();

    [GeneratedRegex(@"^\s*(?:\d+[.)]|[-*•])\s+(?<text>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();
}
