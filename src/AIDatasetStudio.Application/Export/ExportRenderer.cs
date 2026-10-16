using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Writes a record as one line of the target format (SDD §13.1). The stored record is never
/// changed: the conversion happens only here (ADR-004). Pure, so every format is covered by
/// golden tests.
/// </summary>
public static class ExportRenderer
{
    // Turkish and other non-ASCII text stays readable; JSON Lines files are never embedded in HTML.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The record as one JSON line (without the line break).
    /// </summary>
    /// <exception cref="ArgumentException">The format cannot represent the record; see <see cref="ExportFormats.CanWrite"/>.</exception>
    public static string Render(ExportFormat format, RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!ExportFormats.CanWrite(format, payload))
        {
            throw new ArgumentException($"A {payload.Type} record cannot be written as {format}.", nameof(payload));
        }

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            switch (format, payload)
            {
                case (ExportFormat.OpenAi, PreferencePayload p):
                    WriteOpenAiPreference(writer, p);
                    break;
                case (ExportFormat.OpenAi, ToolCallingPayload p):
                    WriteOpenAiTools(writer, p);
                    break;
                case (ExportFormat.ShareGpt, ToolCallingPayload p):
                    WriteShareGptTools(writer, p);
                    break;
                case (ExportFormat.OpenAi, _):
                    WriteMessages(writer, "messages", "role", "content", Turns(payload), OpenAiRole, reasoningName: "reasoning_content");
                    break;
                case (ExportFormat.ShareGpt, _):
                    WriteMessages(writer, "conversations", "from", "value", Turns(payload), ShareGptRole);
                    break;
                case (ExportFormat.Alpaca, _):
                    WriteAlpaca(writer, payload);
                    break;
                case (ExportFormat.HuggingFaceDpo, PreferencePayload p):
                    writer.WriteString("prompt", p.Prompt);
                    writer.WriteString("chosen", p.Chosen);
                    writer.WriteString("rejected", p.Rejected);
                    break;
                case (ExportFormat.ChatMl, _):
                    writer.WriteString("text", ChatMlText(Turns(payload)));
                    break;
                case (ExportFormat.Llama3, _):
                    writer.WriteString("text", Llama3Text(Turns(payload)));
                    break;
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The conversation every format is written from (the common intermediate model, SDD §13.3):
    /// an instruction becomes a user turn (with its input below it) and an assistant turn.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Turns(RecordPayload payload) => payload switch
    {
        InstructionPayload p =>
        [
            new ChatMessage(ChatRole.User, string.IsNullOrWhiteSpace(p.Input) ? p.Instruction : p.Instruction + "\n\n" + p.Input),
            new ChatMessage(ChatRole.Assistant, p.Output, p.Reasoning),
        ],
        ChatPayload p => p.SystemPrompt is { } system ? [new ChatMessage(ChatRole.System, system), .. p.Messages] : p.Messages,
        _ => [], // preference pairs have their own shapes
    };

    // Reasoning goes into its own field when the format has one (reasoningName), otherwise
    // in front of the answer (ADR-052).
    private static void WriteMessages(
        Utf8JsonWriter writer, string arrayName, string roleName, string contentName, IReadOnlyList<ChatMessage> turns, Func<ChatRole, string> role,
        string? reasoningName = null)
    {
        writer.WriteStartArray(arrayName);
        foreach (var turn in turns)
        {
            writer.WriteStartObject();
            writer.WriteString(roleName, role(turn.Role));
            if (reasoningName is null)
            {
                writer.WriteString(contentName, ReasoningText.Inline(turn.Reasoning, turn.Content));
            }
            else
            {
                writer.WriteString(contentName, turn.Content);
                if (turn.Reasoning is { } reasoning)
                {
                    writer.WriteString(reasoningName, reasoning);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // OpenAI's preference fine-tuning shape (SDD §13.3).
    private static void WriteOpenAiPreference(Utf8JsonWriter writer, PreferencePayload p)
    {
        writer.WriteStartObject("input");
        WriteMessages(writer, "messages", "role", "content", [new ChatMessage(ChatRole.User, p.Prompt)], OpenAiRole);
        writer.WriteEndObject();
        WriteMessages(writer, "preferred_output", "role", "content", [new ChatMessage(ChatRole.Assistant, p.Chosen)], OpenAiRole);
        WriteMessages(writer, "non_preferred_output", "role", "content", [new ChatMessage(ChatRole.Assistant, p.Rejected)], OpenAiRole);
    }

    // OpenAI's function calling shape (SDD §13.3, ADR-053): calls in "tool_calls" with their
    // arguments as a JSON string, each result in a "tool" message naming its call, the functions
    // in "tools". Axolotl and Hugging Face chat templates read the same shape.
    private static void WriteOpenAiTools(Utf8JsonWriter writer, ToolCallingPayload p)
    {
        writer.WriteStartArray("messages");
        if (p.SystemPrompt is { } system)
        {
            WriteTurn(writer, "system", system);
        }

        var callNumber = 0;
        foreach (var message in p.Messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                WriteTurn(writer, "user", message.Content);
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("role", "assistant");
            writer.WriteString("content", message.Content);
            if (message.Reasoning is { } reasoning)
            {
                writer.WriteString("reasoning_content", reasoning);
            }

            var first = callNumber + 1;
            if (message.ToolCalls.Count > 0)
            {
                writer.WriteStartArray("tool_calls");
                foreach (var call in message.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", CallId(++callNumber));
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", ToolCalls.Minify(call.Arguments));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            for (var i = 0; i < message.ToolCalls.Count; i++)
            {
                if (message.ToolCalls[i].Result is { } result)
                {
                    writer.WriteStartObject();
                    writer.WriteString("role", "tool");
                    writer.WriteString("tool_call_id", CallId(first + i));
                    writer.WriteString("content", result);
                    writer.WriteEndObject();
                }
            }
        }

        writer.WriteEndArray();
        if (p.Tools.Count > 0)
        {
            writer.WriteStartArray("tools");
            foreach (var tool in p.Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WritePropertyName("function");
                ToolCalls.Function(tool)!.Value.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
    }

    // LLaMA-Factory's ShareGPT shape: a "function_call" turn holds the calls as a JSON list with
    // their arguments as objects, one "observation" turn the results (LLaMA-Factory's own OpenAI
    // reader joins several results this way), "tools" the functions as a JSON text. Text the
    // assistant writes with its calls goes in front of LLaMA-Factory's <tool_call> tags.
    private static void WriteShareGptTools(Utf8JsonWriter writer, ToolCallingPayload p)
    {
        writer.WriteStartArray("conversations");
        if (p.SystemPrompt is { } system)
        {
            WriteTurn(writer, "system", system, "from", "value");
        }

        foreach (var message in p.Messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                WriteTurn(writer, "human", message.Content, "from", "value");
                continue;
            }

            if (message.ToolCalls.Count == 0)
            {
                WriteTurn(writer, "gpt", ReasoningText.Inline(message.Reasoning, message.Content), "from", "value");
                continue;
            }

            var calls = "[" + string.Join(", ", message.ToolCalls.Select(c => $"{{\"name\": {JsonSerializer.Serialize(c.Name, StringOptions)}, \"arguments\": {ToolCalls.Minify(c.Arguments)}}}")) + "]";
            var text = string.IsNullOrWhiteSpace(message.Content) ? calls : message.Content + "<tool_call>" + calls + "</tool_call>";
            WriteTurn(writer, "function_call", ReasoningText.Inline(message.Reasoning, text), "from", "value");
            if (message.ToolCalls.All(c => c.Result is not null))
            {
                WriteTurn(writer, "observation", string.Join("\n</tool_response>\n<tool_response>\n", message.ToolCalls.Select(c => c.Result)), "from", "value");
            }
        }

        writer.WriteEndArray();
        if (p.Tools.Count > 0)
        {
            writer.WriteString("tools", "[" + string.Join(", ", p.Tools.Select(t => ToolCalls.Write(ToolCalls.Function(t)!.Value))) + "]");
        }
    }

    private static string CallId(int number) => "call_" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteTurn(Utf8JsonWriter writer, string role, string content, string roleName = "role", string contentName = "content")
    {
        writer.WriteStartObject();
        writer.WriteString(roleName, role);
        writer.WriteString(contentName, content);
        writer.WriteEndObject();
    }

    private static void WriteAlpaca(Utf8JsonWriter writer, RecordPayload payload)
    {
        switch (payload)
        {
            case InstructionPayload p:
                writer.WriteString("instruction", p.Instruction);
                writer.WriteString("input", p.Input);
                writer.WriteString("output", ReasoningText.Inline(p.Reasoning, p.Output));
                break;
            case ChatPayload p:
                writer.WriteString("instruction", p.Messages[0].Content);
                writer.WriteString("input", string.Empty);
                writer.WriteString("output", ReasoningText.Inline(p.Messages[1].Reasoning, p.Messages[1].Content));
                // Read by LLaMA-Factory and other Alpaca loaders; others ignore it.
                if (p.SystemPrompt is { } system)
                {
                    writer.WriteString("system", system);
                }

                break;
        }
    }

    private static string ChatMlText(IReadOnlyList<ChatMessage> turns)
    {
        var text = new StringBuilder();
        foreach (var turn in turns)
        {
            text.Append("<|im_start|>").Append(OpenAiRole(turn.Role)).Append('\n').Append(ReasoningText.Inline(turn.Reasoning, turn.Content)).Append("<|im_end|>\n");
        }

        return text.ToString();
    }

    // The official Llama 3 Instruct template: a blank line after each header.
    private static string Llama3Text(IReadOnlyList<ChatMessage> turns)
    {
        var text = new StringBuilder("<|begin_of_text|>");
        foreach (var turn in turns)
        {
            text.Append("<|start_header_id|>").Append(OpenAiRole(turn.Role)).Append("<|end_header_id|>\n\n").Append(ReasoningText.Inline(turn.Reasoning, turn.Content)).Append("<|eot_id|>");
        }

        return text.ToString();
    }

    private static string OpenAiRole(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        _ => "assistant",
    };

    private static string ShareGptRole(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "human",
        _ => "gpt",
    };
}
