using System.Text.Json;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Persistence.Converters;

namespace AIDatasetStudio.Infrastructure.Tests.Persistence;

/// <summary>
/// The payload JSON is part of the workspace file format. These tests lock its exact shape;
/// if one fails after a refactoring, existing files would no longer load.
/// </summary>
public sealed class PayloadJsonTests
{
    [Fact]
    public void Instruction_payload_uses_sdd_field_names()
    {
        var json = PayloadJson.Serialize(new InstructionPayload("Özetle", "Metin", "Özet"));

        Assert.Equal("""{"$type":"instruction","instruction":"Özetle","input":"Metin","output":"Özet"}""", json);
    }

    [Fact]
    public void Chat_payload_uses_sdd_field_names()
    {
        var payload = new ChatPayload("Sen bir İK asistanısın.", [
            new ChatMessage(ChatRole.User, "İzin hakkım?"),
            new ChatMessage(ChatRole.Assistant, "14 gün."),
        ]);

        var json = PayloadJson.Serialize(payload);

        Assert.Equal(
            """{"$type":"chat","system_prompt":"Sen bir İK asistanısın.","messages":[{"role":"user","content":"İzin hakkım?"},{"role":"assistant","content":"14 gün."}]}""",
            json);
    }

    [Fact]
    public void Chat_payload_without_system_prompt_omits_the_field()
    {
        var json = PayloadJson.Serialize(new ChatPayload(null, [new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal("""{"$type":"chat","messages":[{"role":"user","content":"hi"}]}""", json);
    }

    [Fact]
    public void Preference_payload_uses_sdd_field_names()
    {
        var json = PayloadJson.Serialize(new PreferencePayload("p", "c", "r"));

        Assert.Equal("""{"$type":"preference","prompt":"p","chosen":"c","rejected":"r"}""", json);
    }

    [Fact]
    public void Reasoning_is_written_only_where_there_is_some()
    {
        Assert.Equal(
            """{"$type":"instruction","instruction":"2+2?","input":"","output":"4","reasoning":"İki artı iki."}""",
            PayloadJson.Serialize(new InstructionPayload("2+2?", "", "4", "İki artı iki.")));
        Assert.Equal(
            """{"$type":"chat","messages":[{"role":"user","content":"2+2?"},{"role":"assistant","content":"4","reasoning":"İki artı iki."}]}""",
            PayloadJson.Serialize(new ChatPayload(null, [new ChatMessage(ChatRole.User, "2+2?"), new ChatMessage(ChatRole.Assistant, "4", "İki artı iki.")])));
    }

    public static TheoryData<RecordPayload> Payloads() =>
    [
        new InstructionPayload("i", "", "o\nwith newline"),
        new InstructionPayload("i", "", "o", "r\nstep 2"),
        new ChatPayload("sys", [new ChatMessage(ChatRole.User, "q"), new ChatMessage(ChatRole.Assistant, "a")]),
        new ChatPayload(null, [new ChatMessage(ChatRole.User, "q"), new ChatMessage(ChatRole.Assistant, "a", "r")]),
        new ChatPayload(null, []),
        new PreferencePayload("p", "```python\nx[::-1]\n```", "loop"),
    ];

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Payloads_round_trip(RecordPayload payload)
    {
        var restored = PayloadJson.Deserialize(PayloadJson.Serialize(payload));

        Assert.Equal(payload, restored);
        Assert.Equal(payload.ComputeContentHash(), restored.ComputeContentHash());
    }

    [Fact]
    public void Discriminator_may_appear_anywhere()
    {
        var payload = PayloadJson.Deserialize("""{"output":"o","$type":"instruction","instruction":"i"}""");

        Assert.Equal(new InstructionPayload("i", "", "o"), payload);
    }

    [Fact]
    public void Unknown_payload_kind_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => PayloadJson.Deserialize("""{"$type":"mystery","x":1}"""));
    }
}
