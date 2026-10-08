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
public sealed partial class QAPairReader
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
                    if (Read(_text.ToString(start, _scanned - start + 1)) is { } pair && Add(pair))
                    {
                        found.Add(pair);
                    }

                    break;
            }
        }

        return found;
    }

    /// <summary>
    /// Ends the answer. When it had no JSON at all, its "Q: … A: …" lines are read.
    /// </summary>
    /// <returns>The pairs found only now.</returns>
    public IReadOnlyList<QAPair> Complete()
    {
        if (_sawObject)
        {
            return [];
        }

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

    private bool Add(QAPair pair)
    {
        if (!_questions.Add(pair.Question))
        {
            return false;
        }

        Pairs.Add(pair);
        return true;
    }

    private static QAPair? Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var question = Value(document.RootElement, QuestionKeys);
            var answer = Value(document.RootElement, AnswerKeys);
            return question is { Length: > 0 } && answer is { Length: > 0 } ? new QAPair(question, answer) : null;
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
}
