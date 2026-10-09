using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Application.Tests.AI;

/// <summary>
/// Synthetic variations of a record and the family they belong to (M14, SDD §17.6).
/// </summary>
public sealed class VariationsTests
{
    private static readonly ChatPayload Chat = new("Sen bir mağaza asistanısın.",
    [
        new(ChatRole.User, "Merhaba"),
        new(ChatRole.Assistant, "Merhaba, nasıl yardımcı olabilirim?"),
        new(ChatRole.User, "Kargo ücreti ne kadar?"),
        new(ChatRole.Assistant, "500 TL üzeri siparişlerde ücretsiz."),
        new(ChatRole.User, "  "),
    ]);

    [Fact]
    public void The_question_and_answer_of_each_type_are_found()
    {
        Assert.Equal(
            new VariationSource("Bu cümleyi özetle.", "Kargo 500 TL üzeri ücretsizdir.", "Kargo ücretsiz."),
            Variations.SourceOf(new InstructionPayload(" Bu cümleyi özetle. ", "Kargo 500 TL üzeri ücretsizdir.", "Kargo ücretsiz.")));
        Assert.Equal(new VariationSource("Kargo ücreti ne kadar?", null, "500 TL üzeri siparişlerde ücretsiz."), Variations.SourceOf(Chat));
        Assert.Equal(new VariationSource("İade süresi?", null, "14 gün."), Variations.SourceOf(new PreferencePayload("İade süresi?", "14 gün.", "Bilmiyorum.")));
        Assert.Equal(new VariationSource("Fatura nerede?", null, string.Empty), Variations.SourceOf(new InstructionPayload("Fatura nerede?", "", "")));

        Assert.Null(Variations.SourceOf(new InstructionPayload(" ", "girdi", "çıktı")));
        Assert.Null(Variations.SourceOf(new ChatPayload(null, [new(ChatRole.Assistant, "Merhaba")])));
        Assert.True(Variations.Supports(DatasetType.Chat));
        Assert.False(Variations.Supports(DatasetType.RagTest));
    }

    [Fact]
    public void The_prompt_asks_for_the_count_and_adds_the_rules_that_were_chosen()
    {
        var source = new VariationSource("Kargo ücreti ne kadar?", "Sipariş tutarı 300 TL.", "49,90 TL.");

        var plain = Variations.Prompt(source, new VariationOptions(4, VariationMode.Questions, Casual: false), null);
        Assert.Equal([ChatRole.System, ChatRole.User], plain.Select(m => m.Role));
        Assert.Contains("Write 4 different ways", plain[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("typos", plain[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("write the answer again", plain[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("""[{"question": "..."}]""", plain[0].Content, StringComparison.Ordinal);
        Assert.Equal("Question:\nKargo ücreti ne kadar?\n\nContext that comes with the question (it stays as it is):\nSipariş tutarı 300 TL.\n\nAnswer:\n49,90 TL.", plain[1].Content);

        var all = Variations.Prompt(source, new VariationOptions(40, VariationMode.QuestionsAndAnswers, Casual: true), " Resmî bir dil kullan. ");
        Assert.Contains("Write 10 different ways", all[0].Content, StringComparison.Ordinal);
        Assert.Contains("common typos", all[0].Content, StringComparison.Ordinal);
        Assert.Contains("write the answer again in other words", all[0].Content, StringComparison.Ordinal);
        Assert.Contains("""[{"question": "...", "answer": "..."}]""", all[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("\n\nInstructions for this dataset:\nResmî bir dil kullan.", all[0].Content, StringComparison.Ordinal);

        // Without an answer there is nothing to rewrite.
        var unanswered = Variations.Prompt(source with { Answer = string.Empty }, new VariationOptions(3, VariationMode.QuestionsAndAnswers, Casual: false), null);
        Assert.DoesNotContain("answer\":", unanswered[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Answer:", unanswered[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variation_replaces_the_question_and_keeps_the_rest()
    {
        var variation = new QAPair("Kargo kaç para?", "Tutar 500 TL'yi geçerse kargo bedava.");

        Assert.Equal(
            new InstructionPayload("Kargo kaç para?", "girdi", "500 TL üzeri ücretsiz."),
            Variations.Payload(new InstructionPayload("Kargo ücreti?", "girdi", "500 TL üzeri ücretsiz."), variation, VariationMode.Questions));
        Assert.Equal(
            new InstructionPayload("Kargo kaç para?", "girdi", "Tutar 500 TL'yi geçerse kargo bedava."),
            Variations.Payload(new InstructionPayload("Kargo ücreti?", "girdi", "500 TL üzeri ücretsiz."), variation, VariationMode.QuestionsAndAnswers));
        Assert.Equal(
            new PreferencePayload("Kargo kaç para?", "Tutar 500 TL'yi geçerse kargo bedava.", "Bilmiyorum."),
            Variations.Payload(new PreferencePayload("Kargo ücreti?", "500 TL üzeri ücretsiz.", "Bilmiyorum."), variation, VariationMode.QuestionsAndAnswers));

        // In a chat the last question and its reply change; the system prompt and earlier turns stay.
        var chat = (ChatPayload)Variations.Payload(Chat, variation, VariationMode.QuestionsAndAnswers);
        Assert.Equal(Chat.SystemPrompt, chat.SystemPrompt);
        Assert.Equal(
        [
            "Merhaba", "Merhaba, nasıl yardımcı olabilirim?", "Kargo kaç para?", "Tutar 500 TL'yi geçerse kargo bedava.", "  ",
        ], chat.Messages.Select(m => m.Content));

        // A variation that only asks again keeps the answer even when answers were asked for.
        var asked = (ChatPayload)Variations.Payload(Chat, variation with { Answer = string.Empty }, VariationMode.QuestionsAndAnswers);
        Assert.Equal("500 TL üzeri siparişlerde ücretsiz.", asked.Messages[3].Content);
    }

    [Fact]
    public void Variations_that_only_ask_again_are_read_in_any_shape()
    {
        var objects = new QAPairReader(questionsOnly: true);
        objects.Push("```json\n[{\"question\": \"Kargo kaç para?\"}, {\"question\": \"Kargo ücreti nedir?\", \"answer\": \"Ücretsiz.\"}, {\"answer\": \"Sorusuz.\"}]\n```");
        Assert.Empty(objects.Complete());
        Assert.Equal([new QAPair("Kargo kaç para?", ""), new QAPair("Kargo ücreti nedir?", "Ücretsiz.")], objects.Pairs);

        var strings = new QAPairReader(questionsOnly: true);
        strings.Push("İşte:\n[\"Kargo kaç para?\", \"kargo ne kadr\", \"\", \"Kargo kaç para?\"]");
        Assert.Equal([new QAPair("Kargo kaç para?", ""), new QAPair("kargo ne kadr", "")], strings.Complete());

        var list = new QAPairReader(questionsOnly: true);
        list.Push("Varyasyonlar:\n1. Kargo kaç para?\n2) **Kargo ücreti nedir?**\n- \"kargo ne kadr\"\nBu kadar.");
        Assert.Equal(["Kargo kaç para?", "Kargo ücreti nedir?", "kargo ne kadr"], list.Complete().Select(p => p.Question));

        // Question and answer lines still come as pairs.
        var lines = new QAPairReader(questionsOnly: true);
        lines.Push("Q: Kargo kaç para?\nA: Ücretsiz.");
        Assert.Equal([new QAPair("Kargo kaç para?", "Ücretsiz.")], lines.Complete());

        // Extraction still needs answers.
        var extraction = new QAPairReader();
        extraction.Push("[{\"question\": \"Kargo kaç para?\"}]");
        Assert.Empty(extraction.Pairs);
        var bare = new QAPairReader();
        bare.Push("1. Kargo kaç para?");
        Assert.Empty(bare.Complete());
    }

    [Fact]
    public void Same_and_too_similar_questions_are_told_apart()
    {
        Assert.True(Variations.AreSame("Kargo ücreti ne kadar?", "  kargo ücreti NE kadar "));
        Assert.True(Variations.AreSame("İADE SÜRESİ", "iade süresi?"));
        Assert.False(Variations.AreSame("Kargo ücreti ne kadar?", "Kargo kaç para?"));

        Assert.True(Variations.Similarity("Kargo ücreti ne kadar acaba?", "Kargo ücreti ne kadar acaba ki?") >= Variations.TooSimilar);
        Assert.True(Variations.Similarity("Kargo ücreti ne kadar?", "Siparişimi gönderirken ne kadar ödemem gerekir?") < Variations.TooSimilar);
    }

    [Fact]
    public void A_variation_belongs_to_the_family_of_the_first_record_and_keeps_its_passage()
    {
        var passage = new PassageSource("kılavuz.pdf", "Kargo", "page", 3, 3, "Kargo 500 TL üzeri ücretsizdir.");
        var root = Details(12, passage.ToMetadata());

        var metadata = VariantOf.For(root);
        Assert.Equal(new VariantOf(root.Id, 12), VariantOf.FromMetadata(metadata));
        Assert.Equal(passage, PassageSource.FromMetadata(metadata));
        Assert.Contains("\"variantOf\":{\"id\":\"" + root.Id.ToString("D"), metadata, StringComparison.OrdinalIgnoreCase);

        // A variation of a variation is still in the first record's family.
        var second = VariantOf.For(Details(30, metadata));
        Assert.Equal(new VariantOf(root.Id, 12), VariantOf.FromMetadata(second));

        Assert.Equal(new VariantOf(root.Id, 12), VariantOf.FromMetadata(VariantOf.For(root with { MetadataJson = "{bozuk" })));
        Assert.Null(VariantOf.FromMetadata("{}"));
        Assert.Null(VariantOf.FromMetadata("{\"variantOf\": {\"id\": \"değil\"}}"));
        Assert.Null(VariantOf.FromMetadata("{bozuk"));
        Assert.Null(VariantOf.FromMetadata(null));
    }

    private static RecordDetails Details(long ordinal, string metadata) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ordinal,
        RecordStatus.Draft,
        new InstructionPayload("Kargo ücreti?", null, "Ücretsiz."),
        "hash",
        RecordLineage.Manual(),
        [],
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        metadata);
}
