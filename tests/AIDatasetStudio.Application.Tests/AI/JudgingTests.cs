using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.AI;

/// <summary>
/// A model's judgment of records and of contradictions between them (M15, SDD §17.7).
/// </summary>
public sealed class JudgingTests
{
    [Fact]
    public void The_prompt_rates_the_answer_side_of_each_type()
    {
        var instruction = Judging.Prompt(new InstructionPayload(" Kargo ücreti ne kadar? ", "Sipariş 300 TL.", "49,90 TL."), null, " Kısa yanıtla. ", "Turkish");
        Assert.Equal([ChatRole.System, ChatRole.User], instruction.Select(m => m.Role));
        Assert.StartsWith("You review examples in a dataset that trains an assistant. Rate the answer to the question as a training example, from 1 to 5:", instruction[0].Content, StringComparison.Ordinal);
        Assert.Contains("Write the reason in Turkish:", instruction[0].Content, StringComparison.Ordinal);
        Assert.Contains("""{"score": 4, "reason": "..."}""", instruction[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("grounded", instruction[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"better\"", instruction[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("Instructions for this dataset, which the answer should follow:\nKısa yanıtla.", instruction[0].Content, StringComparison.Ordinal);
        Assert.Equal("Question:\nKargo ücreti ne kadar?\n\nInput:\nSipariş 300 TL.\n\nAnswer:\n49,90 TL.", instruction[1].Content);

        var chat = Judging.Prompt(new ChatPayload("Sen bir mağaza asistanısın.",
        [
            new(ChatRole.User, "Merhaba"),
            new(ChatRole.Assistant, "Merhaba, nasıl yardımcı olabilirim?"),
            new(ChatRole.User, "Kargo ücreti ne kadar?"),
            new(ChatRole.Assistant, "500 TL üzeri ücretsiz."),
        ]), null, null, "English");
        Assert.Contains("Rate the assistant's replies in the conversation (the last one counts most)", chat[0].Content, StringComparison.Ordinal);
        Assert.Equal(
            "Conversation:\n\nSystem: Sen bir mağaza asistanısın.\n\nUser: Merhaba\n\nAssistant: Merhaba, nasıl yardımcı olabilirim?\n\nUser: Kargo ücreti ne kadar?\n\nAssistant: 500 TL üzeri ücretsiz.",
            chat[1].Content);

        var pair = Judging.Prompt(new PreferencePayload("İade süresi?", "14 gün.", "Bilmiyorum."), null, null, "English");
        Assert.Contains("Rate the chosen answer to the prompt", pair[0].Content, StringComparison.Ordinal);
        Assert.Contains("say in \"better\" which answer is better", pair[0].Content, StringComparison.Ordinal);
        Assert.Contains("""{"score": 4, "reason": "...", "better": "chosen"}""", pair[0].Content, StringComparison.Ordinal);
        Assert.Equal("Prompt:\nİade süresi?\n\nChosen answer:\n14 gün.\n\nRejected answer:\nBilmiyorum.", pair[1].Content);

        // Without a rejected answer there is no order to check.
        var single = Judging.Prompt(new PreferencePayload("İade süresi?", "14 gün.", ""), null, null, "English");
        Assert.DoesNotContain("\"better\"", single[0].Content, StringComparison.Ordinal);
        Assert.Equal("Prompt:\nİade süresi?\n\nChosen answer:\n14 gün.", single[1].Content);
    }

    [Fact]
    public void Reasoning_is_shown_to_the_judge_and_weighed()
    {
        var instruction = Judging.Prompt(new InstructionPayload("2+2?", "", "4", "İki artı iki dört eder."), null, null, "English");
        var chat = Judging.Prompt(new ChatPayload(null, [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4", "İki artı iki.")]), null, null, "English");
        var plain = Judging.Prompt(new InstructionPayload("2+2?", "", "4"), null, null, "English");

        Assert.Contains("Mistakes in the reasoning, and reasoning that does not lead to the answer, lower the score.", instruction[0].Content, StringComparison.Ordinal);
        Assert.Equal("Question:\n2+2?\n\nReasoning before the answer:\nİki artı iki dört eder.\n\nAnswer:\n4", instruction[1].Content);
        Assert.Equal("Conversation:\n\nUser: 2+2?\n\nAssistant's reasoning: İki artı iki.\n\nAssistant: 4", chat[1].Content);
        Assert.DoesNotContain("reasoning", plain[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tool_calling_record_is_judged_with_its_functions_calls_and_results()
    {
        var payload = new ToolCallingPayload(null, ["""{ "name": "fee", "description": "Kargo ücreti" }"""],
        [
            new(ChatRole.User, "Kargo ne kadar?"),
            new(ChatRole.Assistant, "", toolCalls: [new ToolCall("fee", "{ }", "49,90 TL")]),
            new(ChatRole.Assistant, "Kargo 49,90 TL."),
        ]);

        var prompt = Judging.Prompt(payload, null, null, "English");

        Assert.True(Judging.Supports(DatasetType.ToolCalling) && Judging.CanJudge(payload));
        Assert.Contains("Rate the assistant's function calls and replies in the conversation", prompt[0].Content, StringComparison.Ordinal);
        Assert.Contains("call the right function with correct arguments", prompt[0].Content, StringComparison.Ordinal);
        Assert.Equal(
            "Functions:\n{\"name\":\"fee\",\"description\":\"Kargo ücreti\"}\n\nConversation:\n\nUser: Kargo ne kadar?\n\nAssistant calls: fee {}\n\nResult of fee: 49,90 TL\n\nAssistant: Kargo 49,90 TL.",
            prompt[1].Content);
        Assert.Equal(("Kargo ne kadar?", "Kargo 49,90 TL."), (Variations.SourceOf(payload)!.Question, Variations.SourceOf(payload)!.Answer));
    }

    [Fact]
    public void An_answer_written_from_a_document_is_checked_against_its_passage()
    {
        var passage = new PassageSource("kılavuz.pdf", "Sipariş › Kargo", "page", 3, 3, "Kargo 500 TL üzeri siparişlerde ücretsizdir.");

        var messages = Judging.Prompt(new InstructionPayload("Kargo ücreti ne kadar?", "", "Ücretsiz."), passage, null, "Turkish");

        Assert.Contains("set \"grounded\" to false when it states something the passage does not say", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("""{"score": 4, "reason": "...", "grounded": true}""", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(
            "Source passage (kılavuz.pdf · Sipariş › Kargo):\nKargo 500 TL üzeri siparişlerde ücretsizdir.\n\nQuestion:\nKargo ücreti ne kadar?\n\nAnswer:\nÜcretsiz.",
            messages[1].Content);
        Assert.StartsWith("Source passage (notlar.txt):", Judging.Prompt(new InstructionPayload("Soru?", "", "Yanıt."), passage with { File = "notlar.txt", Section = "" }, null, "Turkish")[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_records_that_ask_and_answer_something_are_judged()
    {
        Assert.True(Judging.CanJudge(new InstructionPayload("Soru?", "", "Yanıt.")));
        Assert.False(Judging.CanJudge(new InstructionPayload("Soru?", "", " ")));
        Assert.False(Judging.CanJudge(new InstructionPayload(" ", "", "Yanıt.")));
        Assert.True(Judging.CanJudge(new ChatPayload(null, [new(ChatRole.User, "Soru?"), new(ChatRole.Assistant, "Yanıt.")])));
        Assert.False(Judging.CanJudge(new ChatPayload(null, [new(ChatRole.User, "Soru?")])));
        Assert.True(Judging.CanJudge(new PreferencePayload("Soru?", "İyi.", "")));
        Assert.False(Judging.CanJudge(new PreferencePayload("Soru?", "", "Kötü.")));
        Assert.True(Judging.Supports(DatasetType.Preference));
        Assert.False(Judging.Supports(DatasetType.RagTest));
    }

    [Theory]
    [InlineData("{\"score\": 4, \"reason\": \"Doğru ama kısa.\"}", 4, "Doğru ama kısa.", JudgeFlags.None)]
    [InlineData("Değerlendirmem:\n```json\n{\"Score\": \"5/5\", \"Gerekçe\": \"Eksiksiz.\", \"grounded\": \"yes\"}\n```", 5, "Eksiksiz.", JudgeFlags.None)]
    [InlineData("{\"puan\": 2.5, \"reason\": \"Kaynakta yok.\", \"grounded\": false}", 3, "Kaynakta yok.", JudgeFlags.Ungrounded)]
    [InlineData("{\"rating\": 3, \"explanation\": \"Reddedilen daha iyi.\", \"better\": \"rejected\"}", 3, "Reddedilen daha iyi.", JudgeFlags.RejectedBetter)]
    [InlineData("{\"score\": 4, \"reason\": \"İkisi de olur. {not}\", \"better\": \"Same\"}", 4, "İkisi de olur. {not}", JudgeFlags.NoPreference)]
    [InlineData("{\"score\": 2}", 2, "", JudgeFlags.None)]
    [InlineData("**Puan:** 2/5\nYanıt soruyu karşılamıyor.", 2, "Yanıt soruyu karşılamıyor.", JudgeFlags.None)]
    [InlineData("Yanıt doğru ve açık. 5/5", 5, "Yanıt doğru ve açık.", JudgeFlags.None)]
    public void Verdicts_are_read_in_any_shape(string answer, int score, string reason, JudgeFlags flags) =>
        Assert.Equal(new JudgeVerdict(score, reason, flags), Judging.Read(answer));

    [Theory]
    [InlineData("{\"score\": 8, \"reason\": \"10 üzerinden.\"}")]
    [InlineData("{\"score\": \"iyi\"}")]
    [InlineData("{\"reason\": \"Puan yok.\"}")]
    [InlineData("Bu yanıt iyi görünüyor.")]
    [InlineData("12/50 bulundu.")]
    public void Answers_without_a_score_from_1_to_5_are_not_read(string answer) => Assert.Null(Judging.Read(answer));

    [Fact]
    public void Two_answers_are_compared_and_the_verdict_read()
    {
        var messages = Judging.ConflictPrompt(
            new VariationSource("İade süresi nedir?", null, "14 gün."),
            new VariationSource("Kaç gün içinde iade edebilirim?", "Ürün: ayakkabı", "30 gün."),
            "Turkish");

        Assert.Contains("Decide whether their answers contradict each other", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("Write the reason in Turkish, in one short sentence", messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("""{"contradicts": true, "reason": "..."}""", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(
            "Example A\nQuestion:\nİade süresi nedir?\nAnswer:\n14 gün.\n\nExample B\nQuestion:\nKaç gün içinde iade edebilirim?\nInput:\nÜrün: ayakkabı\nAnswer:\n30 gün.",
            messages[1].Content);

        Assert.Equal(new ConflictVerdict(true, "14 gün ile 30 gün farklı."), Judging.ReadConflict("```json\n{\"contradicts\": true, \"reason\": \"14 gün ile 30 gün farklı.\"}\n```"));
        Assert.Equal(new ConflictVerdict(false, ""), Judging.ReadConflict("{\"Contradiction\": \"no\"}"));
        Assert.Null(Judging.ReadConflict("Çelişmiyorlar."));
    }
}
