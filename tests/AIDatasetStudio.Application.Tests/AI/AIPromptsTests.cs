using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.AI;

public sealed class AIPromptsTests
{
    [Fact]
    public void An_instruction_is_answered_with_its_input_and_the_dataset_guidance()
    {
        var messages = AIPrompts.For(new InstructionPayload("Bu metni özetle.", "Uzun bir metin…", ""), AIWriteTarget.Answer, "Yanıtlar en fazla iki cümle.")!;

        Assert.Equal([ChatRole.System, ChatRole.User], messages.Select(m => m.Role));
        Assert.Contains("Answer in the language of the question.", messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("Instructions for this dataset:\nYanıtlar en fazla iki cümle.", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal("Bu metni özetle.\n\nUzun bir metin…", messages[1].Content);

        Assert.Null(AIPrompts.For(new InstructionPayload(" ", "", "x"), AIWriteTarget.Answer, null));
        Assert.DoesNotContain("Instructions for this dataset", AIPrompts.For(new InstructionPayload("Soru?", "", ""), AIWriteTarget.Answer, "  ")![0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reply_follows_the_conversation_under_its_own_system_prompt()
    {
        var chat = new ChatPayload("Sen bir kargo asistanısın.",
        [
            new ChatMessage(ChatRole.User, "Merhaba"),
            new ChatMessage(ChatRole.Assistant, "Merhaba! Size nasıl yardımcı olabilirim?"),
            new ChatMessage(ChatRole.User, "Kargom nerede?"),
        ]);

        var messages = AIPrompts.For(chat, AIWriteTarget.Reply, null)!;

        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        Assert.StartsWith("Sen bir kargo asistanısın.\n\n", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(3, AIPrompts.ReplyIndex(chat));

        // A last assistant turn is written again from the turns before it.
        var answered = new ChatPayload(null, [.. chat.Messages, new ChatMessage(ChatRole.Assistant, "Eski yanıt")]);
        Assert.Equal(3, AIPrompts.ReplyIndex(answered));
        Assert.Equal(4, AIPrompts.For(answered, AIWriteTarget.Reply, null)!.Count);

        Assert.Null(AIPrompts.For(new ChatPayload(null, [new ChatMessage(ChatRole.Assistant, "Merhaba")]), AIWriteTarget.Reply, null));
        Assert.Null(AIPrompts.For(new ChatPayload(null, []), AIWriteTarget.Reply, null));
    }

    [Fact]
    public void A_rejected_answer_is_asked_to_be_worse_than_the_chosen_one()
    {
        var pair = new PreferencePayload("İade süresi nedir?", "Teslimattan sonra 14 gün içinde iade edebilirsiniz.", "");

        var chosen = AIPrompts.For(pair, AIWriteTarget.Chosen, null)!;
        var rejected = AIPrompts.For(pair, AIWriteTarget.Rejected, "Türkçe yaz.")!;

        Assert.Equal("İade süresi nedir?", chosen[1].Content);
        Assert.Contains("rejected answer of a preference (DPO) training pair", rejected[0].Content, StringComparison.Ordinal);
        Assert.Contains("Türkçe yaz.", rejected[0].Content, StringComparison.Ordinal);
        Assert.Contains("A good answer, for reference:\nTeslimattan sonra 14 gün içinde iade edebilirsiniz.", rejected[1].Content, StringComparison.Ordinal);
        Assert.Equal("İade süresi nedir?", AIPrompts.For(new PreferencePayload(pair.Prompt, "", ""), AIWriteTarget.Rejected, null)![1].Content);
    }

    [Fact]
    public void Each_dataset_type_offers_its_targets()
    {
        Assert.Equal([AIWriteTarget.Answer], AIPrompts.TargetsFor(DatasetType.Instruction));
        Assert.Equal([AIWriteTarget.Reply], AIPrompts.TargetsFor(DatasetType.Chat));
        Assert.Equal([AIWriteTarget.Chosen, AIWriteTarget.Rejected], AIPrompts.TargetsFor(DatasetType.Preference));
        Assert.Throws<ArgumentException>(() => AIPrompts.For(new InstructionPayload("a", "", ""), AIWriteTarget.Reply, null));
    }

    [Theory]
    [InlineData(new[] { "<think>Kullanıcı selam veriyor.</think>\n\nSelam!" }, "Selam!")]
    [InlineData(new[] { "<thi", "nk>düşün", "üyorum</th", "ink>", " Yanıt", " burada." }, "Yanıt burada.")]
    [InlineData(new[] { "a < b ve ", "c <th", "en d" }, "a < b ve c <then d")]
    [InlineData(new[] { "Kod: `<think>` etiketi" }, "Kod: `")]
    [InlineData(new[] { "<think>bitmeyen düşünce" }, "")]
    [InlineData(new[] { "  \n", "Merhaba", " dünya" }, "Merhaba dünya")]
    public void Reasoning_blocks_are_left_out_wherever_pieces_split_them(string[] pieces, string expected)
    {
        var filter = new ReasoningFilter();
        var text = string.Concat(pieces.Select(filter.Push)) + filter.Flush();

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://[::1]:8000/v1", true)]
    [InlineData("http://192.168.1.20:11434/v1", true)]
    [InlineData("http://10.0.0.5/v1", true)]
    [InlineData("http://172.20.1.1/v1", true)]
    [InlineData("http://gpu-sunucu:8000/v1", true)]
    [InlineData("http://ollama.local:11434/v1", true)]
    [InlineData("http://172.32.0.1/v1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://8.8.8.8/v1", false)]
    [InlineData("not a url", true)]
    public void Local_addresses_are_told_from_remote_ones(string url, bool local)
    {
        Assert.Equal(local, AIProviders.IsLocal(url));
    }
}
