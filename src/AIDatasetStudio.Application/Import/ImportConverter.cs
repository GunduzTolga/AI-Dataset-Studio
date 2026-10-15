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
            SourceLayout.OpenAiMessages or SourceLayout.OpenAiTools when plan.TargetType == DatasetType.ToolCalling =>
                FromToolConversation(row, row.Json?.GetPropertyOrNull("messages")),
            SourceLayout.ShareGpt or SourceLayout.ShareGptTools when plan.TargetType == DatasetType.ToolCalling =>
                FromToolConversation(row, row.Json?.GetPropertyOrNull("conversations")),
            SourceLayout.XLam => FromXLam(row),
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

    /// <summary>
    /// A conversation with function calls (SDD §4.4): OpenAI's <c>tool_calls</c> and <c>tool</c>
    /// results (matched by <c>tool_call_id</c>, otherwise in order), or ShareGPT's
    /// <c>function_call</c> and <c>observation</c> turns. A result is stored with its call.
    /// </summary>
    private static ImportRowResult FromToolConversation(ISourceRow row, JsonElement? conversation)
    {
        if (conversation is not { ValueKind: JsonValueKind.Array } array)
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        var system = row.Json?.GetPropertyOrNull("system")?.GetStringOrNull();
        var turns = new List<ToolTurn>();
        foreach (var item in array.EnumerateArray())
        {
            var roleName = (item.GetPropertyOrNull("role") ?? item.GetPropertyOrNull("from"))?.GetStringOrNull();
            if (item.ValueKind != JsonValueKind.Object || roleName is null)
            {
                return Fail(row, ImportErrorCode.NoConversation);
            }

            var text = (item.GetPropertyOrNull("content") ?? item.GetPropertyOrNull("value")) is { } content ? ContentText(content) : string.Empty;
            var kind = ImportLayouts.Normalize(roleName);
            switch (kind)
            {
                case "system" when turns.Count == 0 && system is null:
                    system = text;
                    break;
                case "functioncall":
                    if (ReadCalls(text) is not { } called)
                    {
                        return Fail(row, ImportErrorCode.InvalidJson);
                    }

                    turns.Add(called);
                    break;
                case "tool" or "function" or "observation" or "ipython":
                    if (!Answer(turns, text, item.GetPropertyOrNull("tool_call_id")?.GetStringOrNull(), kind == "observation"))
                    {
                        return Fail(row, ImportErrorCode.ToolResultUnmatched);
                    }

                    break;
                default:
                    var role = RoleFrom(roleName);
                    if (role is not (ChatRole.User or ChatRole.Assistant))
                    {
                        return Fail(row, ImportErrorCode.UnknownRole, roleName);
                    }

                    turns.Add(role == ChatRole.User ? new ToolTurn(ChatRole.User, text, null) : AssistantTurn(item, text));
                    break;
            }
        }

        if (!turns.Any(t => t.Role == ChatRole.User) && !turns.Any(t => t.Role == ChatRole.Assistant))
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        var tools = ReadTools(row.Json?.GetPropertyOrNull("tools") ?? row.Json?.GetPropertyOrNull("functions"));
        return new ImportRowResult(row.Number, new ToolCallingPayload(system, tools, [.. turns.Select(t => t.ToMessage())]), null);
    }

    // xLAM / APIGen: a question, the functions it may use and the calls that answer it.
    private static ImportRowResult FromXLam(ISourceRow row)
    {
        var query = row.Json?.GetPropertyOrNull("query") is { } asked ? ContentText(asked) : string.Empty;
        var answers = Parse(row.Json?.GetPropertyOrNull("answers"));
        if (string.IsNullOrWhiteSpace(query) || answers is not { ValueKind: JsonValueKind.Array or JsonValueKind.Object } calls)
        {
            return Fail(row, ImportErrorCode.NoConversation);
        }

        var assistant = new ToolTurn(ChatRole.Assistant, string.Empty, null);
        assistant.Calls.AddRange(CallsIn(calls));
        var tools = ReadTools(row.Json?.GetPropertyOrNull("tools"));
        return new ImportRowResult(row.Number, new ToolCallingPayload(null, tools, [new ChatMessage(ChatRole.User, query), assistant.ToMessage()]), null);
    }

    // An assistant message: its text, reasoning and calls (OpenAI "tool_calls", or the older "function_call").
    private static ToolTurn AssistantTurn(JsonElement item, string text)
    {
        var reasoning = ReasoningKeys.Select(key => item.GetPropertyOrNull(key)?.GetStringOrNull()).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        if (reasoning is null)
        {
            (reasoning, text) = ReasoningText.Split(text);
        }

        var turn = new ToolTurn(ChatRole.Assistant, text, reasoning);
        if (item.GetPropertyOrNull("tool_calls") is { ValueKind: JsonValueKind.Array } calls)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetPropertyOrNull("function") ?? call;
                turn.Calls.Add(new ToolCallDraft(
                    call.GetPropertyOrNull("id")?.GetStringOrNull(),
                    function.GetPropertyOrNull("name")?.GetStringOrNull() ?? string.Empty,
                    Arguments(function.GetPropertyOrNull("arguments"))));
            }
        }
        else if (item.GetPropertyOrNull("function_call") is { ValueKind: JsonValueKind.Object } legacy)
        {
            turn.Calls.AddRange(CallsIn(legacy));
        }

        return turn;
    }

    // A ShareGPT "function_call" turn: the calls as JSON (one or a list), after any reasoning and
    // text; LLaMA-Factory and Hermes wrap them in <tool_call> tags when the assistant also writes.
    private static ToolTurn? ReadCalls(string value)
    {
        var (reasoning, text) = ReasoningText.Split(value);
        var open = text.IndexOf("<tool_call>", StringComparison.Ordinal);
        var written = open < 0 ? string.Empty : text[..open].Trim();
        var blocks = open < 0
            ? [text]
            : text[open..].Split("<tool_call>", StringSplitOptions.RemoveEmptyEntries)
                .Select(b => b.Replace("</tool_call>", string.Empty, StringComparison.Ordinal))
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .ToList();
        var turn = new ToolTurn(ChatRole.Assistant, written, reasoning);
        foreach (var block in blocks)
        {
            if (ParseText(block) is not { ValueKind: JsonValueKind.Array or JsonValueKind.Object } calls)
            {
                return null;
            }

            turn.Calls.AddRange(CallsIn(calls));
        }

        return turn.Calls.Count > 0 ? turn : null;
    }

    // Each {"name", "arguments"} of a list (or a single call).
    private static IEnumerable<ToolCallDraft> CallsIn(JsonElement calls) =>
        (calls.ValueKind == JsonValueKind.Array ? calls.EnumerateArray().ToList() : [calls])
            .Where(c => c.ValueKind == JsonValueKind.Object)
            .Select(c => new ToolCallDraft(null, c.GetPropertyOrNull("name")?.GetStringOrNull() ?? string.Empty,
                Arguments(c.GetPropertyOrNull("arguments") ?? c.GetPropertyOrNull("parameters"))));

    // Gives a result to the call it answers: by id, otherwise the first one still waiting. A
    // ShareGPT observation answers all waiting calls, split as LLaMA-Factory joins them.
    private static bool Answer(List<ToolTurn> turns, string result, string? callId, bool all)
    {
        var waiting = turns.Count > 0 ? turns[^1].Calls.Where(c => c.Result is null).ToList() : [];
        if (waiting.Count == 0)
        {
            return false;
        }

        if (all && waiting.Count > 1)
        {
            var parts = result.Split("\n</tool_response>\n<tool_response>\n");
            if (parts.Length != waiting.Count)
            {
                return false;
            }

            for (var i = 0; i < parts.Length; i++)
            {
                waiting[i].Result = parts[i];
            }

            return true;
        }

        (waiting.FirstOrDefault(c => callId is not null && c.Id == callId) ?? waiting[0]).Result = result;
        return true;
    }

    // Tools as a list of objects or a JSON text of one; each is stored as its function, indented.
    private static List<string> ReadTools(JsonElement? value) =>
        Parse(value) switch
        {
            { ValueKind: JsonValueKind.Array } list => [.. list.EnumerateArray().Select(Tool)],
            { ValueKind: JsonValueKind.Object } one => [Tool(one)],
            _ => [],
        };

    private static string Tool(JsonElement tool)
    {
        var item = tool.ValueKind == JsonValueKind.String && ParseText(tool.GetString()!) is { } parsed ? parsed : tool;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText();
        }

        return ToolCalls.Pretty(item.GetPropertyOrNull("function") is { ValueKind: JsonValueKind.Object } function ? function : item);
    }

    // Arguments as compact JSON: an object as it is, a JSON text of one parsed, nothing as {}.
    private static string Arguments(JsonElement? value) => Parse(value) switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "{}",
        { ValueKind: JsonValueKind.String } text => text.GetString()!,
        { } json => ToolCalls.Write(json),
    };

    // A JSON value, or the JSON a string holds.
    private static JsonElement? Parse(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } text && ParseText(text.GetString()!) is { } parsed ? parsed : value;

    private static JsonElement? ParseText(string text)
    {
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

    // A turn being read: calls get their results from later messages.
    private sealed class ToolTurn(ChatRole role, string content, string? reasoning)
    {
        public ChatRole Role { get; } = role;

        public List<ToolCallDraft> Calls { get; } = [];

        public ChatMessage ToMessage() => new(Role, content, reasoning, [.. Calls.Select(c => new ToolCall(c.Name, c.Arguments, c.Result))]);
    }

    private sealed class ToolCallDraft(string? id, string name, string arguments)
    {
        public string? Id { get; } = id;

        public string Name { get; } = name;

        public string Arguments { get; } = arguments;

        public string? Result { get; set; }
    }

    private static ImportRowResult Fail(ISourceRow row, ImportErrorCode code, string? detail = null) =>
        new(row.Number, null, new ImportRowError(row.Number, code, detail));

    private static JsonElement? GetPropertyOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? GetStringOrNull(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
