using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
                case (ExportFormat.OpenAi, _):
                    WriteMessages(writer, "messages", "role", "content", Turns(payload), OpenAiRole);
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
            new ChatMessage(ChatRole.Assistant, p.Output),
        ],
        ChatPayload p => p.SystemPrompt is { } system ? [new ChatMessage(ChatRole.System, system), .. p.Messages] : p.Messages,
        _ => [], // preference pairs have their own shapes
    };

    private static void WriteMessages(
        Utf8JsonWriter writer, string arrayName, string roleName, string contentName, IReadOnlyList<ChatMessage> turns, Func<ChatRole, string> role)
    {
        writer.WriteStartArray(arrayName);
        foreach (var turn in turns)
        {
            writer.WriteStartObject();
            writer.WriteString(roleName, role(turn.Role));
            writer.WriteString(contentName, turn.Content);
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

    private static void WriteAlpaca(Utf8JsonWriter writer, RecordPayload payload)
    {
        switch (payload)
        {
            case InstructionPayload p:
                writer.WriteString("instruction", p.Instruction);
                writer.WriteString("input", p.Input);
                writer.WriteString("output", p.Output);
                break;
            case ChatPayload p:
                writer.WriteString("instruction", p.Messages[0].Content);
                writer.WriteString("input", string.Empty);
                writer.WriteString("output", p.Messages[1].Content);
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
            text.Append("<|im_start|>").Append(OpenAiRole(turn.Role)).Append('\n').Append(turn.Content).Append("<|im_end|>\n");
        }

        return text.ToString();
    }

    // The official Llama 3 Instruct template: a blank line after each header.
    private static string Llama3Text(IReadOnlyList<ChatMessage> turns)
    {
        var text = new StringBuilder("<|begin_of_text|>");
        foreach (var turn in turns)
        {
            text.Append("<|start_header_id|>").Append(OpenAiRole(turn.Role)).Append("<|end_header_id|>\n\n").Append(turn.Content).Append("<|eot_id|>");
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
