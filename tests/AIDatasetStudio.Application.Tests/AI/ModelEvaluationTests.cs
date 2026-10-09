using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.AI;

/// <summary>
/// Model evaluation against the dataset's own answers (M22, ADR-044).
/// </summary>
public sealed class ModelEvaluationTests
{
    [Fact]
    public void Questions_go_to_the_model_as_a_trained_model_would_see_them()
    {
        var instruction = ModelEvaluation.Item(new InstructionPayload(" Kargo ne kadar? ", "Sipariş 300 TL.", " 49,90 TL. "))!;
        Assert.Equal([(ChatRole.User, "Kargo ne kadar?\n\nSipariş 300 TL.")], instruction.Messages.Select(m => (m.Role, m.Content)));
        Assert.Equal("49,90 TL.", instruction.Reference);

        var chat = ModelEvaluation.Item(new ChatPayload("Mağaza asistanısın.",
        [
            new ChatMessage(ChatRole.User, "Merhaba"),
            new ChatMessage(ChatRole.Assistant, "Merhaba, nasıl yardımcı olabilirim?"),
            new ChatMessage(ChatRole.User, "İade süresi?"),
            new ChatMessage(ChatRole.Assistant, "14 gün."),
        ]))!;
        Assert.Equal(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            chat.Messages.Select(m => m.Role));
        Assert.Equal("Mağaza asistanısın.", chat.Messages[0].Content);
        Assert.Equal("14 gün.", chat.Reference);
        Assert.Equal("User: Merhaba\n\nAssistant: Merhaba, nasıl yardımcı olabilirim?\n\nUser: İade süresi?", chat.Question);

        // The app's own writing rules are not part of the question.
        Assert.DoesNotContain(chat.Messages, m => m.Content.Contains("dataset", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("İyi yanıt", ModelEvaluation.Item(new PreferencePayload("Soru", "İyi yanıt", "Kötü yanıt"))!.Reference);

        // Nothing to ask or nothing to compare with.
        Assert.Null(ModelEvaluation.Item(new InstructionPayload("Soru", "", " ")));
        Assert.Null(ModelEvaluation.Item(new ChatPayload(null, [new ChatMessage(ChatRole.User, "Soru")])));
        Assert.Null(ModelEvaluation.Item(new ChatPayload(null, [new ChatMessage(ChatRole.Assistant, "Merhaba")])));
    }

    [Fact]
    public void The_grader_compares_with_the_reference_and_its_verdict_is_read()
    {
        var item = ModelEvaluation.Item(new InstructionPayload("İade süresi?", "", "Teslimattan itibaren 14 gün."))!;

        var prompt = ModelEvaluation.GradePrompt(item, " 14 gün içinde iade edebilirsiniz. ", "Turkish");

        Assert.Equal(ChatRole.System, prompt[0].Role);
        Assert.Contains("Write the reason in Turkish", prompt[0].Content, StringComparison.Ordinal);
        Assert.Contains("{\"score\": 4, \"reason\": \"...\"}", prompt[0].Content, StringComparison.Ordinal);
        Assert.Equal("Question:\nİade süresi?\n\nReference answer:\nTeslimattan itibaren 14 gün.\n\nModel's answer:\n14 gün içinde iade edebilirsiniz.", prompt[1].Content);
        Assert.EndsWith("Model's answer:\n(empty)", ModelEvaluation.GradePrompt(item, "  ", "English")[1].Content, StringComparison.Ordinal);

        var result = ModelEvaluation.Result(item, "14 gün içinde iade edebilirsiniz.", "```json\n{\"score\": 4, \"reason\": \"Doğru ama başlangıcı söylemiyor.\"}\n```");
        Assert.Equal((4, "Doğru ama başlangıcı söylemiyor."), (result.Score, result.Reason));
        // "14 gün" in common: 2 of 5 answer words, 2 of 4 reference words → 4/9.
        Assert.Equal(4.0 / 9, result.F1, 6);
        Assert.Null(ModelEvaluation.Result(item, "x", "Bilmiyorum").Score);
    }

    [Fact]
    public void Word_overlap_is_token_f1()
    {
        Assert.Equal(1, ModelEvaluation.TokenF1("14 gün.", "14 GÜN"));
        Assert.Equal(0, ModelEvaluation.TokenF1("30 gün", "iki hafta"));
        Assert.Equal(1, ModelEvaluation.TokenF1("", " "));
        Assert.Equal(0, ModelEvaluation.TokenF1("", "14 gün"));

        // "teslimattan 14 gün" against "14 gün içinde": 2 common of 3 and 3 → 2/3.
        Assert.Equal(2.0 / 3, ModelEvaluation.TokenF1("teslimattan 14 gün", "14 gün içinde"), 6);
    }

    [Fact]
    public void A_seed_draws_the_same_sample_and_new_records_do_not_move_it()
    {
        // Ids from a fixed seed, so the test asks the same thing on every run.
        var random = new Random(1);
        List<Guid> NewIds(int count) => [.. Enumerable.Range(0, count).Select(_ =>
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        })];
        var ids = NewIds(200);

        var sample = ModelEvaluation.Sample(ids, 20, seed: 42);

        Assert.Equal(20, sample.Distinct().Count());
        Assert.Equal(sample, ModelEvaluation.Sample(Enumerable.Reverse(ids), 20, seed: 42));
        Assert.NotEqual(sample, ModelEvaluation.Sample(ids, 20, seed: 7));

        // Records added later can only push the last ones out; the rest stay, in the same order.
        var grown = ModelEvaluation.Sample([.. ids, .. NewIds(50)], 20, seed: 42);
        var kept = grown.Where(ids.Contains).ToList();
        Assert.NotEmpty(kept);
        Assert.Equal(sample.Take(kept.Count), kept);
        Assert.Equal(200, ModelEvaluation.Sample(ids, 500, seed: 1).Count);
    }

    [Fact]
    public void A_summary_averages_the_scores_it_could_read()
    {
        var summary = ModelEvaluation.Summarize(
        [
            new EvaluationResult("a", 5, "", 1),
            new EvaluationResult("b", 3, "", 0.5),
            new EvaluationResult("c", null, "", 0),
            new EvaluationResult("d", 3, "", 0.5),
        ]);

        Assert.Equal((4, 3, 11.0 / 3, 0.5), (summary.Answered, summary.Scored, summary.AverageScore!.Value, summary.AverageF1!.Value));
        Assert.Equal([0, 0, 2, 0, 1], summary.Counts);
        Assert.Null(ModelEvaluation.Summarize([]).AverageScore);
    }
}
