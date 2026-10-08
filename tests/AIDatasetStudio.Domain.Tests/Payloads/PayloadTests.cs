using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Domain.Tests.Payloads;

public sealed class PayloadTests
{
    [Fact]
    public void Null_text_fields_become_empty_strings()
    {
        var payload = new InstructionPayload(null, null, null);

        Assert.Equal(string.Empty, payload.Instruction);
        Assert.Equal(string.Empty, payload.Input);
        Assert.Equal(string.Empty, payload.Output);
    }

    [Fact]
    public void Chat_payloads_with_equal_messages_are_equal()
    {
        var first = new ChatPayload("sys", [new(ChatRole.User, "hi")]);
        var second = new ChatPayload("sys", [new(ChatRole.User, "hi")]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Chat_payload_copies_the_message_list()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var payload = new ChatPayload(null, messages);

        messages.Add(new ChatMessage(ChatRole.Assistant, "later"));

        Assert.Single(payload.Messages);
    }

    [Fact]
    public void Empty_system_prompt_is_treated_as_absent()
    {
        Assert.Null(new ChatPayload("", []).SystemPrompt);
    }
}
