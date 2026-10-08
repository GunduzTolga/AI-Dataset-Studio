using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Export;

public sealed class ExportSplitTests
{
    [Fact]
    public void Assignments_are_fixed_for_a_seed()
    {
        // Pinned: a change here would move records between files of existing exports (ADR-018).
        var parts = Enumerable.Range(0, 12).Select(i => ExportSplit.Assign(SplitOptions.Standard, "id:" + i)).ToList();

        Assert.Equal(parts, Enumerable.Range(0, 12).Select(i => ExportSplit.Assign(SplitOptions.Standard, "id:" + i)));
        Assert.Equal(PinnedParts, string.Join(",", parts));
        Assert.NotEqual(parts, Enumerable.Range(0, 12).Select(i => ExportSplit.Assign(SplitOptions.Standard with { Seed = 7 }, "id:" + i)));
    }

    [Fact]
    public void Shares_are_met_over_many_records()
    {
        var counts = Enumerable.Range(0, 20_000)
            .Select(_ => ExportSplit.Assign(SplitOptions.Standard, "id:" + Guid.NewGuid()))
            .GroupBy(part => part)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.InRange(counts[SplitPart.Train], 15_700, 16_300);
        Assert.InRange(counts[SplitPart.Validation], 1_800, 2_200);
        Assert.InRange(counts[SplitPart.Test], 1_800, 2_200);
    }

    [Fact]
    public void Empty_shares_get_no_records()
    {
        var parts = Enumerable.Range(0, 2_000).Select(i => ExportSplit.Assign(SplitOptions.TrainValidation, "id:" + i)).ToHashSet();

        Assert.DoesNotContain(SplitPart.Test, parts);
        Assert.All(Enumerable.Range(0, 200), i => Assert.Equal(SplitPart.Train, ExportSplit.Assign(new SplitOptions(100, 0, 0), "id:" + i)));
        Assert.Equal([SplitPart.Train, SplitPart.Validation], SplitOptions.TrainValidation.Parts);
    }

    [Fact]
    public void Records_with_the_same_question_share_a_group()
    {
        var first = ExportSplit.GroupKey(new InstructionPayload("Şifremi unuttum!", "", "Sıfırlayın."), Guid.NewGuid());
        var second = ExportSplit.GroupKey(new InstructionPayload("şifremi  UNUTTUM", "", "Destek ekibine yazın."), Guid.NewGuid());
        var other = ExportSplit.GroupKey(new InstructionPayload("Başka bir soru", "", "Cevap"), Guid.NewGuid());
        var chat = ExportSplit.GroupKey(new ChatPayload(null, [new ChatMessage(ChatRole.User, "Şifremi unuttum"), new ChatMessage(ChatRole.Assistant, "x")]), Guid.NewGuid());
        var pair = ExportSplit.GroupKey(new PreferencePayload("Şifremi unuttum", "a", "b"), Guid.NewGuid());

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(first, chat); // the same prompt side
        Assert.Equal(chat, pair);
    }

    [Fact]
    public void Records_without_a_question_use_their_id()
    {
        var id = Guid.NewGuid();

        Assert.Equal("id:" + id.ToString("D"), ExportSplit.GroupKey(new InstructionPayload("  ", "", "cevap"), id));
    }

    [Theory]
    [InlineData(80, 10, 10, true)]
    [InlineData(90, 10, 0, true)]
    [InlineData(100, 0, 0, true)]
    [InlineData(0, 50, 50, false)]
    [InlineData(80, 10, 5, false)]
    [InlineData(80, -10, 30, false)]
    public void Shares_must_add_up_to_100_with_some_training_data(int train, int validation, int test, bool valid)
    {
        Assert.Equal(valid, new SplitOptions(train, validation, test).IsValid);
    }

    private const string PinnedParts = "Test,Train,Train,Train,Test,Test,Train,Train,Train,Train,Train,Train";
}
