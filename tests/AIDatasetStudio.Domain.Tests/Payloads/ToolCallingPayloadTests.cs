using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Domain.Tests.Payloads;

/// <summary>
/// Tool calling records (SDD §4.4): calls live on assistant turns with their results.
/// </summary>
public sealed class ToolCallingPayloadTests
{
    private const string Tool = """{"name": "get_forecast", "parameters": {"type": "object"}}""";

    private static ToolCallingPayload Example(string? result = "{\"temp\": 19}", string reasoning = "") => new(
        "Hava durumu asistanısın.",
        [Tool],
        [
            new(ChatRole.User, "Yarın Kadıköy'de hava nasıl?"),
            new(ChatRole.Assistant, "", reasoning, [new ToolCall("get_forecast", "{\"location\": \"Kadıköy\"}", result)]),
            new(ChatRole.Assistant, "19 derece, parçalı bulutlu."),
        ]);

    [Fact]
    public void Calls_are_kept_on_assistant_turns_trimmed_and_a_blank_result_is_none()
    {
        var call = new ToolCall(" get_forecast ", "\n{\"a\": 1}\n", "  ");

        Assert.Equal(("get_forecast", "{\"a\": 1}", (string?)null), (call.Name, call.Arguments, call.Result));
        Assert.Empty(new ChatMessage(ChatRole.User, "q", toolCalls: [call]).ToolCalls);
        Assert.Single(new ChatMessage(ChatRole.Assistant, "", toolCalls: [call]).ToolCalls);
        Assert.Equal(["{\"name\": \"x\"}"], new ToolCallingPayload(null, [" {\"name\": \"x\"} "], []).Tools);
    }

    [Fact]
    public void Payloads_compare_their_tools_turns_and_calls_by_value()
    {
        Assert.Equal(Example(), Example());
        Assert.Equal(Example().GetHashCode(), Example().GetHashCode());
        Assert.NotEqual(Example(), Example(result: "{\"temp\": 20}"));
        Assert.Equal(DatasetType.ToolCalling, Example().Type);
    }

    [Fact]
    public void A_chat_cannot_call_functions()
    {
        Assert.Throws<ArgumentException>(() => new ChatPayload(null,
            [new ChatMessage(ChatRole.User, "q"), new ChatMessage(ChatRole.Assistant, "", toolCalls: [new ToolCall("f", "{}")])]));
    }

    [Fact]
    public void The_hash_tells_results_reasoning_and_tools_apart()
    {
        var hashes = new[]
        {
            Example().ComputeContentHash(),
            Example(result: null).ComputeContentHash(),
            Example(reasoning: "Konum ve gün belli.").ComputeContentHash(),
            new ToolCallingPayload(Example().SystemPrompt, [], Example().Messages).ComputeContentHash(),
            new ToolCallingPayload(Example().SystemPrompt, [Tool, Tool], Example().Messages).ComputeContentHash(),
        };

        Assert.Equal(hashes.Length, hashes.Distinct().Count());

        // A call cannot be read as the next turn, nor a tool as a turn.
        var call = new ToolCallingPayload(null, [], [new(ChatRole.Assistant, "a", toolCalls: [new ToolCall("User", "b")])]);
        var turns = new ToolCallingPayload(null, [], [new(ChatRole.Assistant, "a"), new(ChatRole.User, "b")]);
        Assert.NotEqual(call.ComputeContentHash(), turns.ComputeContentHash());
        Assert.NotEqual(
            new ToolCallingPayload(null, ["User"], []).ComputeContentHash(),
            new ToolCallingPayload(null, [], [new(ChatRole.User, "")]).ComputeContentHash());
    }

    [Fact]
    public void Everything_is_searched_and_the_question_is_previewed()
    {
        var payload = Example(reasoning: "Konum belli.");

        Assert.Equal(
            ["Hava durumu asistanısın.", Tool, "Yarın Kadıköy'de hava nasıl?", "Konum belli.", "", "get_forecast", "{\"location\": \"Kadıköy\"}", "{\"temp\": 19}", "19 derece, parçalı bulutlu."],
            payload.GetSearchableTexts());
        Assert.Equal("Yarın Kadıköy'de hava nasıl?", payload.GetPreviewText());
    }
}
