using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Records;

/// <summary>
/// Tool definitions, arguments and the shape of tool calling conversations (SDD §4.4, ADR-053).
/// </summary>
public sealed class ToolCallsTests
{
    private const string Tool = """{"name": "get_forecast", "parameters": {"type": "object", "required": ["location", "days"]}}""";

    [Fact]
    public void A_tool_names_its_function_plainly_or_in_OpenAIs_wrapper()
    {
        Assert.Equal("get_forecast", ToolCalls.NameOf(Tool));
        Assert.Equal("get_forecast", ToolCalls.NameOf("""{"type": "function", "function": {"name": "get_forecast"}}"""));
        Assert.Null(ToolCalls.NameOf("""{"description": "no name"}"""));
        Assert.Null(ToolCalls.NameOf("""{"name": "  "}"""));
        Assert.Null(ToolCalls.NameOf("[]"));
        Assert.Null(ToolCalls.NameOf("{name:"));
        Assert.Equal(["location", "days"], ToolCalls.RequiredOf(Tool));
        Assert.Equal("get_forecast", ToolCalls.NameOf(ToolCalls.NewTool.Replace("\"name\": \"\"", "\"name\": \"get_forecast\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Json_is_written_compactly_with_its_text_readable()
    {
        Assert.Equal("""{"şehir":"İzmir","gün":[1,2]}""", ToolCalls.Minify("{ \"şehir\" : \"İzmir\",\n \"gün\": [1, 2] }"));
        Assert.Equal("not json", ToolCalls.Minify("not json"));
    }

    public static TheoryData<string, bool> Conversations() => new()
    {
        // user → call with result → answer
        { "u a(r) a", true },
        // the example ends with the call, as xLAM does
        { "u a()", true },
        // several rounds and turns
        { "u a(r,r) a(r) a u a", true },
        // no tool is needed
        { "u a", true },
        // the assistant does not start; the user does not end
        { "a", false },
        { "u a u", false },
        // a question cannot follow calls, an answer cannot follow an answer
        { "u a(r) u a", false },
        { "u a a", false },
        // a call before an answer needs its result; the last call has none
        { "u a() a", false },
        { "u a(r,) a", false },
        { "u a(r)", false },
        { "", false },
    };

    [Theory]
    [MemberData(nameof(Conversations))]
    public void A_conversation_is_well_formed_when_its_turns_follow_each_other(string turns, bool expected)
    {
        Assert.Equal(expected, ToolCalls.IsWellFormed(new ToolCallingPayload(null, [Tool], Parse(turns))));
    }

    [Fact]
    public void Tools_need_names_and_calls_object_arguments()
    {
        var calls = Parse("u a(r) a");
        Assert.False(ToolCalls.IsWellFormed(new ToolCallingPayload(null, ["""{"description": "x"}"""], calls)));
        Assert.False(ToolCalls.IsWellFormed(new ToolCallingPayload(null, [Tool],
            [calls[0], new ChatMessage(ChatRole.Assistant, "", toolCalls: [new ToolCall("get_forecast", "[1]", "r")]), calls[2]])));
        Assert.True(ToolCalls.IsInOrder(new ToolCallingPayload(null, ["""{"description": "x"}"""], calls)));
    }

    // "u" a user turn, "a" an assistant turn, "a(r,)" one calling twice: the first with a result.
    internal static IReadOnlyList<ChatMessage> Parse(string turns) =>
    [
        .. turns.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(turn => turn[0] == 'u'
            ? new ChatMessage(ChatRole.User, "soru")
            : new ChatMessage(ChatRole.Assistant, turn.Length == 1 ? "yanıt" : "", toolCalls: turn.Length == 1
                ? null
                : [.. turn[2..^1].Split(',').Select(result => new ToolCall("get_forecast", """{"location": "Kadıköy"}""", result == "r" ? "{\"temp\": 19}" : null))])),
    ];
}
