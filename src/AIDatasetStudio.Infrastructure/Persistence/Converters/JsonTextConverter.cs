using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIDatasetStudio.Infrastructure.Persistence.Converters;

/// <summary>
/// Stores text that holds a JSON object or array (tool definitions, call arguments and results,
/// SDD §4.4) as that JSON, so the file reads like the schema; any other text, such as a draft
/// that does not parse yet, as a string. The text reads back exactly as it was written:
/// <see cref="JsonElement.GetRawText"/> keeps its spacing and line breaks.
/// </summary>
internal sealed class JsonTextConverter : JsonConverter<string>
{
    public static readonly JsonTextConverter Instance = new();

    public override bool HandleNull => false;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString()!;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (IsStructure(value))
        {
            writer.WriteRawValue(value, skipInputValidation: true);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    // Only an object or array with nothing around it reads back as the same text.
    private static bool IsStructure(string value)
    {
        if (value.Length == 0 || value[0] is not ('{' or '[') || value[^1] is not ('}' or ']'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// A list of <see cref="JsonTextConverter"/> texts.
/// </summary>
internal sealed class JsonTextListConverter : JsonConverter<IReadOnlyList<string>>
{
    public static readonly JsonTextListConverter Instance = new();

    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a list.");
        }

        var items = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            items.Add(JsonTextConverter.Instance.Read(ref reader, typeof(string), options));
        }

        return items;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            JsonTextConverter.Instance.Write(writer, item, options);
        }

        writer.WriteEndArray();
    }
}
