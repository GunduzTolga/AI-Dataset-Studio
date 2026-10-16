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

    [Fact]
    public void Reasoning_is_kept_on_answers_only_and_blank_reasoning_is_none()
    {
        Assert.Null(new InstructionPayload("q", "", "a", " \n").Reasoning);
        Assert.Equal("Adım adım.", new InstructionPayload("q", "", "a", "Adım adım.").Reasoning);
        Assert.Null(new ChatMessage(ChatRole.User, "q", "Kullanıcı düşünmez.").Reasoning);
        Assert.Null(new ChatMessage(ChatRole.Assistant, "a", "").Reasoning);
        Assert.Equal("Adım adım.", new ChatMessage(ChatRole.Assistant, "a", "Adım adım.").Reasoning);
    }

    [Fact]
    public void Reasoning_is_searched_and_scanned_but_not_previewed()
    {
        var instruction = new InstructionPayload("Soru", "", "Cevap", "Düşünce");
        var chat = new ChatPayload(null, [new(ChatRole.User, "Soru"), new(ChatRole.Assistant, "Cevap", "Düşünce")]);

        Assert.Equal(["Soru", "", "Cevap", "Düşünce"], instruction.GetSearchableTexts());
        Assert.Equal(["Soru", "Düşünce", "Cevap"], chat.GetSearchableTexts());
        Assert.Equal("Soru", chat.GetPreviewText());
        Assert.NotEqual(chat, new ChatPayload(null, [new(ChatRole.User, "Soru"), new(ChatRole.Assistant, "Cevap")]));
    }
}
