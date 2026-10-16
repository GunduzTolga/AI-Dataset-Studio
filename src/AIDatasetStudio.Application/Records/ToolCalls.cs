using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Records;

/// <summary>
/// Reads the JSON of tool calling records (SDD §4.4, ADR-053): tool definitions, call arguments,
/// and whether a conversation has the shape the training formats can write.
/// </summary>
public static class ToolCalls
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonWriterOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true };

    /// <summary>
    /// A new tool's definition: a function with an object of parameters.
    /// </summary>
    public const string NewTool = """
        {
          "name": "",
          "description": "",
          "parameters": {
            "type": "object",
            "properties": {},
            "required": []
          }
        }
        """;

    /// <summary>
    /// The JSON object a text holds, or <c>null</c> when it is not one.
    /// </summary>
    public static JsonElement? Object(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The function a tool definition describes: the object itself, or the one inside OpenAI's
    /// <c>{"type": "function", "function": {...}}</c>; <c>null</c> when it names none.
    /// </summary>
    public static JsonElement? Function(string tool) => Object(tool) is { } root
        ? root.TryGetProperty("function", out var inner) && inner.ValueKind == JsonValueKind.Object ? Named(inner) : Named(root)
        : null;

    /// <summary>
    /// The name of the function a tool definition describes, or <c>null</c>.
    /// </summary>
    public static string? NameOf(string tool) => Function(tool)?.GetProperty("name").GetString();

    /// <summary>
    /// The parameters a tool's function requires.
    /// </summary>
    public static IReadOnlyList<string> RequiredOf(string tool) =>
        Function(tool) is { } function
        && function.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? [.. required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!)]
            : [];

    /// <summary>
    /// The JSON written without spaces and line breaks (non-ASCII text stays readable); a text
    /// that is not JSON is returned as it is.
    /// </summary>
    public static string Minify(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Write(document.RootElement);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// A JSON value as compact text.
    /// </summary>
    public static string Write(JsonElement element) => Write(element, Compact);

    /// <summary>
    /// A JSON value indented, as the editor shows tool definitions.
    /// </summary>
    public static string Pretty(JsonElement element) => Write(element, Indented).ReplaceLineEndings("\n");

    private static string Write(JsonElement element, JsonWriterOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Whether the training formats can write the record (the SQL twin is in the export):
    /// every tool names a function; the conversation starts with the user, answers come after
    /// questions and after the results of the calls before them, and it ends with the
    /// assistant, either answering or calling functions whose results are not given; every call's
    /// arguments are a JSON object.
    /// </summary>
    public static bool IsWellFormed(ToolCallingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return IsInOrder(payload)
            && payload.Tools.All(t => NameOf(t) is not null)
            && payload.Messages.SelectMany(m => m.ToolCalls).All(c => Object(c.Arguments) is not null);
    }

    /// <summary>
    /// Whether the turns are in an order the formats can write: the user starts, an answer follows
    /// a question or the results of the calls before it, a question follows an answer that calls
    /// nothing, and the assistant ends, either answering or calling without results.
    /// </summary>
    public static bool IsInOrder(ToolCallingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var messages = payload.Messages;
        if (messages.Count == 0 || messages[0].Role != ChatRole.User || messages[^1].Role != ChatRole.Assistant
            || messages[^1].ToolCalls.Any(c => c.Result is not null))
        {
            return false;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role is not (ChatRole.User or ChatRole.Assistant) || (i > 0 && !Follows(messages[i - 1], messages[i])))
            {
                return false;
            }
        }

        return true;
    }

    // A question follows an answer; an answer follows a question or the results of the calls before it.
    private static bool Follows(ChatMessage previous, ChatMessage message) => message.Role == ChatRole.User
        ? previous.Role == ChatRole.Assistant && previous.ToolCalls.Count == 0
        : previous.Role == ChatRole.User || (previous.ToolCalls.Count > 0 && previous.ToolCalls.All(c => c.Result is not null));

    private static JsonElement? Named(JsonElement function) =>
        function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString()!.Trim().Length > 0 ? function : null;
}
