using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Records;

/// <summary>
/// The reasoning of an answer written in front of it as a &lt;think&gt; block, and read back (ADR-052).
/// </summary>
public sealed class ReasoningTextTests
{
    [Fact]
    public void A_think_block_the_answer_starts_with_is_the_reasoning()
    {
        Assert.Equal(("Önce topla.", "4"), ReasoningText.Split("<think>\nÖnce topla.\n</think>\n\n4"));
        Assert.Equal(("Önce topla.", "4"), ReasoningText.Split("  <think>Önce topla.</think>4"));
        Assert.Equal(((string?)null, "4"), ReasoningText.Split("<think>\n\n</think>\n\n4"));

        // Anywhere else, or never closed, it is part of the answer.
        Assert.Equal(((string?)null, "Cevap <think>x</think>"), ReasoningText.Split("Cevap <think>x</think>"));
        Assert.Equal(((string?)null, "<think>yarım kaldı"), ReasoningText.Split("<think>yarım kaldı"));
    }

    [Fact]
    public void Inline_and_split_are_each_others_reverse()
    {
        var text = ReasoningText.Inline(" İki artı iki.\n", "4");

        Assert.Equal("<think>\nİki artı iki.\n</think>\n\n4", text);
        Assert.Equal(("İki artı iki.", "4"), ReasoningText.Split(text));
        Assert.Equal("4", ReasoningText.Inline(null, "4"));
    }

    [Fact]
    public void A_record_can_be_written_without_its_reasoning()
    {
        var instruction = new InstructionPayload("2+2?", "", "4", "İki artı iki.");
        var chat = new ChatPayload("Kısa yaz.", [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4", "İki artı iki.")]);
        var plain = new PreferencePayload("p", "c", "r");

        Assert.True(ReasoningText.Has(instruction) && ReasoningText.Has(chat));
        Assert.Equal(new InstructionPayload("2+2?", "", "4"), ReasoningText.Without(instruction));
        Assert.Equal(new ChatPayload("Kısa yaz.", [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4")]), ReasoningText.Without(chat));
        Assert.Same(plain, ReasoningText.Without(plain));
        Assert.False(ReasoningText.Has(plain));

        // The calls stay; only the thinking goes.
        var call = new ToolCall("f", "{}", "1");
        var tools = new ToolCallingPayload(null, ["{\"name\": \"f\"}"], [new(ChatRole.User, "?"), new(ChatRole.Assistant, "", "Önce f.", [call]), new(ChatRole.Assistant, "1")]);
        Assert.True(ReasoningText.Has(tools));
        Assert.Equal(
            new ToolCallingPayload(null, tools.Tools, [new(ChatRole.User, "?"), new(ChatRole.Assistant, "", toolCalls: [call]), new(ChatRole.Assistant, "1")]),
            ReasoningText.Without(tools));
    }
}
