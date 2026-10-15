using System.Text;
using System.Text.Json;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Import;

/// <summary>
/// Turns one source row into a payload of the target dataset type, or explains why it cannot.
/// </summary>
public static class ImportConverter
{
    // Where an assistant message keeps its reasoning: OpenAI-compatible APIs, Qwen3 and DeepSeek data.
    private static readonly string[] ReasoningKeys = ["reasoning_content", "reasoning", "thinking"];

    public static ImportRowResult Convert(ISourceRow row, ImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(plan);

        if (!ImportLayouts.Supports(plan.Layout, plan.TargetType))
        {
            return Fail(row, ImportErrorCode.UnsupportedLayout);
        }

        return plan.Layout switch
        {
            SourceLayout.OpenAiMessages => FromConversation(row, row.Json?.GetPropertyOrNull("messages"), plan.TargetType),
            SourceLayout.ShareGpt => FromConversation(row, row.Json?.GetPropertyOrNull("conversations"), plan.TargetType),
            SourceLayout.OpenAiPreference => FromOpenAiPreference(row),
            SourceLayout.Transcript => FromTranscript(row, plan),
            _ => FromColumns(row, plan),
        };
    }

    /// <summary>
    /// A conversation of a log: the customer's messages are user turns, the agent's (or, without
    /// an agent, everyone else's) assistant turns. Messages in a row from one side are one turn;
    /// the conversation starts with the customer and ends with an answer.
    /// </summary>
    private static ImportRowResult FromTranscript(ISourceRow row, ImportPlan plan)
    {
        if (row is not ITranscriptRow transcript || !plan.Mapping.TryGetValue(TargetField.User, out var customer))
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        plan.Mapping.TryGetValue(TargetField.Assistant, out var agent);
        var turns = new List<(ChatRole Role, StringBuilder Text)>();
        foreach (var message in transcript.Messages)
        {
            ChatRole? role = message.Speaker == customer ? ChatRole.User
                : agent is null || message.Speaker == agent ? ChatRole.Assistant
                : null;
            if (role is not { } side || string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            if (turns.Count > 0 && turns[^1].Role == side)
            {
                turns[^1].Text.Append('\n').Append(message.Text.Trim());
            }
            else if (turns.Count > 0 || side == ChatRole.User)
            {
                turns.Add((side, new StringBuilder(message.Text.Trim())));
            }
        }

        if (turns.Count > 0 && turns[^1].Role == ChatRole.User)
        {
            turns.RemoveAt(turns.Count - 1);
        }

        if (turns.Count < 2)
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        var messages = turns.Select(t => new ChatMessage(t.Role, t.Text.ToString())).ToList();
        if (plan.TargetType == DatasetType.Chat)
        {
            return new ImportRowResult(row.Number, new ChatPayload(null, messages), null);
        }

        return messages.Count == 2
            ? new ImportRowResult(row.Number, new InstructionPayload(messages[0].Content, string.Empty, messages[1].Content), null)
            : Fail(row, ImportErrorCode.MultiTurnNotSupported);
    }

    private static ImportRowResult FromColumns(ISourceRow row, ImportPlan plan)
    {
        string Text(TargetField field) =>
            plan.Mapping.TryGetValue(field, out var column) ? row.GetText(column) ?? string.Empty : string.Empty;

        JsonElement? Element(TargetField field) =>
            plan.Mapping.TryGetValue(field, out var column) ? row.GetElement(column) : null;

        // The reasoning column, or else a <think> block the answer starts with.
        (string? Reasoning, string Answer) Answer(TargetField field) =>
            Text(TargetField.Reasoning) is { Length: > 0 } reasoning ? (reasoning, Text(field)) : ReasoningText.Split(Text(field));

        RecordPayload payload;
        switch (plan.TargetType)
        {
            case DatasetType.Instruction:
                var output = Answer(TargetField.Output);
                payload = new InstructionPayload(Text(TargetField.Instruction), Text(TargetField.Input), output.Answer, output.Reasoning);
                break;

            case DatasetType.Chat:
                var user = Text(TargetField.User);
                var assistant = Answer(TargetField.Assistant);
                if (user.Length == 0 && assistant.Answer.Length == 0 && assistant.Reasoning is null)
                {
                    return Fail(row, ImportErrorCode.EmptyRecord);
                }

                payload = new ChatPayload(
                    Text(TargetField.SystemPrompt),
                    [new ChatMessage(ChatRole.User, user), new ChatMessage(ChatRole.Assistant, assistant.Answer, assistant.Reasoning)]);
                break;

            case DatasetType.Preference:
                var prompt = Text(TargetField.Prompt);
                // HF datasets often store chosen/rejected as full conversations whose user turn is the prompt.
                if (prompt.Length == 0 && Element(TargetField.Chosen) is { ValueKind: JsonValueKind.Array } chosenConversation
                    && TryReadMessages(chosenConversation, out var messages, out _))
                {
                    prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
                }

                payload = new PreferencePayload(prompt, Text(TargetField.Chosen), Text(TargetField.Rejected));
                break;

            default:
                return Fail(row, ImportErrorCode.UnsupportedLayout);
        }

        return payload.GetSearchableTexts().All(string.IsNullOrWhiteSpace)
            ? Fail(row, ImportErrorCode.EmptyRecord)
            : new ImportRowResult(row.Number, payload, null);
    }

    private static ImportRowResult FromConversation(ISourceRow row, JsonElement? conversation, DatasetType target)
    {
        if (conversation is not { ValueKind: JsonValueKind.Array } array)
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        if (!TryReadMessages(array, out var messages, out var unknownRole))
        {
            return unknownRole is null
                ? Fail(row, ImportErrorCode.NoConversation)
                : Fail(row, ImportErrorCode.UnknownRole, unknownRole);
        }

        string? systemPrompt = null;
        if (messages.Count > 0 && messages[0].Role == ChatRole.System)
        {
            systemPrompt = messages[0].Content;
            messages.RemoveAt(0);
        }

        if (!messages.Any(m => m.Role is ChatRole.User or ChatRole.Assistant))
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        if (target == DatasetType.Chat)
        {
            return new ImportRowResult(row.Number, new ChatPayload(systemPrompt, messages), null);
        }

        // Instruction: exactly one user turn answered by one assistant turn.
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            return Fail(row, ImportErrorCode.SystemPromptNotSupported);
        }

        if (messages.Count != 2 || messages[0].Role != ChatRole.User || messages[1].Role != ChatRole.Assistant)
        {
            return Fail(row, ImportErrorCode.MultiTurnNotSupported);
        }

        return new ImportRowResult(row.Number, new InstructionPayload(messages[0].Content, string.Empty, messages[1].Content, messages[1].Reasoning), null);
    }

    private static ImportRowResult FromOpenAiPreference(ISourceRow row)
    {
        var input = row.Json?.GetPropertyOrNull("input")?.GetPropertyOrNull("messages");
        if (input is not { ValueKind: JsonValueKind.Array } inputArray)
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        if (!TryReadMessages(inputArray, out var context, out var unknownRole))
        {
            return unknownRole is null
                ? Fail(row, ImportErrorCode.NoConversation)
                : Fail(row, ImportErrorCode.UnknownRole, unknownRole);
        }

        // A preference record stores a single prompt; earlier turns or a system prompt would be lost.
        if (context.Any(m => m.Role == ChatRole.System))
        {
            return Fail(row, ImportErrorCode.SystemPromptNotSupported);
        }

        if (context.Count != 1 || context[0].Role != ChatRole.User)
        {
            return Fail(row, ImportErrorCode.MultiTurnNotSupported);
        }

        var chosen = ExtractText(row.Json?.GetPropertyOrNull("preferred_output")) ?? string.Empty;
        var rejected = ExtractText(row.Json?.GetPropertyOrNull("non_preferred_output")) ?? string.Empty;
        var payload = new PreferencePayload(context[0].Content, chosen, rejected);
        return payload.GetSearchableTexts().All(string.IsNullOrWhiteSpace)
            ? Fail(row, ImportErrorCode.EmptyRecord)
            : new ImportRowResult(row.Number, payload, null);
    }

    /// <summary>
    /// The text of a JSON value: strings as is, a message list as its last assistant turn
    /// (or last message), an object as its "content", anything else as raw JSON.
    /// </summary>
    public static string? ExtractText(JsonElement? value)
    {
        switch (value)
        {
            case null:
            case { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                return null;
            case { ValueKind: JsonValueKind.String } text:
                return text.GetString();
            case { ValueKind: JsonValueKind.Array } array when TryReadMessages(array, out var messages, out _) && messages.Count > 0:
                return (messages.LastOrDefault(m => m.Role == ChatRole.Assistant) ?? messages[^1]).Content;
            case { ValueKind: JsonValueKind.Object } obj when obj.GetPropertyOrNull("content") is { } content:
                return ContentText(content);
            case { } other:
                return other.GetRawText();
        }
    }

    /// <summary>
    /// Reads a list of messages in OpenAI ({role, content}) or ShareGPT ({from, value}) form.
    /// </summary>
    /// <returns><c>false</c> if the list is not made of message objects.</returns>
    private static bool TryReadMessages(JsonElement array, out List<ChatMessage> messages, out string? unknownRole)
    {
        messages = [];
        unknownRole = null;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var roleName = (item.GetPropertyOrNull("role") ?? item.GetPropertyOrNull("from"))?.GetStringOrNull();
            var content = item.GetPropertyOrNull("content") ?? item.GetPropertyOrNull("value");
            if (roleName is null || content is null)
            {
                return false;
            }

            var role = RoleFrom(roleName);
            if (role is null)
            {
                unknownRole ??= roleName;
                continue;
            }

            var text = ContentText(content.Value);
            string? reasoning = null;
            if (role == ChatRole.Assistant)
            {
                // A reasoning field, or else a <think> block the answer starts with.
                reasoning = ReasoningKeys.Select(key => item.GetPropertyOrNull(key)?.GetStringOrNull()).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                if (reasoning is null)
                {
                    (reasoning, text) = ReasoningText.Split(text);
                }
            }

            messages.Add(new ChatMessage(role.Value, text, reasoning));
        }

        return unknownRole is null;
    }

    // OpenAI content may be a string or a list of parts such as {"type": "text", "text": "..."}.
    private static string ContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.GetPropertyOrNull("text")?.GetStringOrNull() is { } text)
                {
                    builder.Append(text);
                }
            }

            return builder.ToString();
        }

        return content.ValueKind is JsonValueKind.Null ? string.Empty : content.GetRawText();
    }

    private static ChatRole? RoleFrom(string name) => ImportLayouts.Normalize(name) switch
    {
        "system" => ChatRole.System,
        "user" or "human" => ChatRole.User,
        "assistant" or "gpt" or "chatgpt" or "bard" or "bing" or "model" or "bot" => ChatRole.Assistant,
        _ => null,
    };

    private static ImportRowResult Fail(ISourceRow row, ImportErrorCode code, string? detail = null) =>
        new(row.Number, null, new ImportRowError(row.Number, code, detail));

    private static JsonElement? GetPropertyOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? GetStringOrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
