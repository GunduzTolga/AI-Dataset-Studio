using AIDatasetStudio.Application.Export;

namespace AIDatasetStudio.Application.Tests.Export;

public sealed class StratifiedSplitTests
{
    [Fact]
    public void Every_stratum_gets_the_exact_shares()
    {
        // 100 legal and 30 technical questions, one record each, plus 7 without a category.
        var groups = Enumerable.Range(0, 100).Select(i => new StratifiedGroup("q:hukuk " + i, "kategori:hukuk", 1))
            .Concat(Enumerable.Range(0, 30).Select(i => new StratifiedGroup("q:teknik " + i, "kategori:teknik", 1)))
            .Concat(Enumerable.Range(0, 7).Select(i => new StratifiedGroup("id:" + i, "", 1)))
            .ToList();

        var parts = ExportSplit.AssignStratified(SplitOptions.Standard with { StratifyBy = "kategori" }, groups);

        Assert.Equal((80, 10, 10), Count(parts, groups, "kategori:hukuk"));
        Assert.Equal((24, 3, 3), Count(parts, groups, "kategori:teknik"));
        Assert.Equal((6, 1, 0), Count(parts, groups, ""));
    }

    [Fact]
    public void Groups_stay_whole_and_count_by_records()
    {
        // One question asked 6 times among 10 records of a stratum: it fills most of training.
        List<StratifiedGroup> groups = [new("q:büyük", "a", 6), .. Enumerable.Range(0, 4).Select(i => new StratifiedGroup("q:küçük " + i, "a", 1))];

        var parts = ExportSplit.AssignStratified(new SplitOptions(60, 20, 20), groups);

        Assert.Equal(groups.Count, parts.Count);
        var records = groups.GroupBy(g => parts[g.GroupKey]).ToDictionary(g => g.Key, g => g.Sum(x => x.RecordCount));
        Assert.InRange(records.GetValueOrDefault(SplitPart.Train), 6, 7);
        Assert.Equal(10, records.Values.Sum());
    }

    [Fact]
    public void Assignments_are_fixed_for_a_seed_and_follow_it()
    {
        var groups = Enumerable.Range(0, 50).Select(i => new StratifiedGroup("q:" + i, "s", 1)).ToList();

        var first = ExportSplit.AssignStratified(SplitOptions.Standard, groups);
        var again = ExportSplit.AssignStratified(SplitOptions.Standard, [.. Enumerable.Reverse(groups)]);
        var other = ExportSplit.AssignStratified(SplitOptions.Standard with { Seed = 7 }, groups);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Without_a_test_share_the_rest_is_validation()
    {
        var groups = Enumerable.Range(0, 11).Select(i => new StratifiedGroup("q:" + i, "s", 1)).ToList();

        var parts = ExportSplit.AssignStratified(SplitOptions.TrainValidation, groups);

        Assert.Equal((10, 1, 0), Count(parts, groups, "s"));
    }

    [Theory]
    [InlineData("kategori", "kategori:hukuk")]
    [InlineData("KATEGORİ", "kategori:hukuk")]
    [InlineData("dil", "dil:en")]
    [InlineData("yok", "")]
    [InlineData(SplitOptions.AnyTag, "dil:en")]
    public void The_stratum_is_the_first_tag_of_the_group(string stratifyBy, string stratum)
    {
        Assert.Equal(stratum, ExportSplit.StratumOf(stratifyBy, ["kategori:teknik", "zor", "dil:en", "kategori:hukuk"]));
        Assert.Equal("", ExportSplit.StratumOf(null, ["kategori:hukuk"]));
        Assert.Equal("", ExportSplit.StratumOf(stratifyBy, []));
    }

    private static (int Train, int Validation, int Test) Count(IReadOnlyDictionary<string, SplitPart> parts, List<StratifiedGroup> groups, string stratum)
    {
        var mine = groups.Where(g => g.Stratum == stratum).Select(g => parts[g.GroupKey]).ToList();
        return (mine.Count(p => p == SplitPart.Train), mine.Count(p => p == SplitPart.Validation), mine.Count(p => p == SplitPart.Test));
    }
}
