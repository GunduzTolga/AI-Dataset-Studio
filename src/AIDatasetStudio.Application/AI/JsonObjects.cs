using System.Text.Json;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// The JSON objects in a model's answer, wherever they are: in a code fence, after a sentence, one
/// inside another. Models are asked for JSON but often wrap it in prose.
/// </summary>
internal static class JsonObjects
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true };

    /// <summary>
    /// Every complete object in <paramref name="text"/> that parses, innermost first.
    /// </summary>
    public static IEnumerable<JsonElement> In(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var starts = new Stack<int>();
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                // Quotes count only inside an object: prose around the JSON may have its own.
                case '"' when starts.Count > 0:
                    inString = true;
                    break;
                case '{':
                    starts.Push(i);
                    break;
                case '}' when starts.Count > 0:
                    var start = starts.Pop();
                    if (Parse(text.Substring(start, i - start + 1)) is { } element)
                    {
                        yield return element;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The value of the first property named like one of <paramref name="keys"/> (case and
    /// surrounding spaces ignored).
    /// </summary>
    public static JsonElement? Property(JsonElement element, IReadOnlyCollection<string> keys)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (keys.Any(k => string.Equals(k, property.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// A string or number property as text, trimmed.
    /// </summary>
    public static string? Text(JsonElement element, IReadOnlyCollection<string> keys) => Property(element, keys) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString()?.Trim(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// A yes/no property: a JSON boolean, or "yes", "no", "true", "false", "evet", "hayır".
    /// </summary>
    public static bool? Flag(JsonElement element, IReadOnlyCollection<string> keys) => Property(element, keys) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } value => value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "evet" or "doğru" => true,
            "no" or "false" or "hayır" or "hayir" or "yanlış" => false,
            _ => null,
        },
        _ => null,
    };

    private static JsonElement? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, Options);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
