using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Judgments;

/// <summary>
/// Judgments and contradictions kept with the content they were made of (M15, SDD §17.7).
/// </summary>
public sealed class JudgmentServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_judgment_is_current_until_the_record_changes_and_shows_in_the_list_filters_and_summary()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Preference, Ct);
        var ids = await AddAsync(ws, datasetId,
            new PreferencePayload("Kargo ücreti ne kadar?", "500 TL üzeri ücretsiz.", "Bilmiyorum."),
            new PreferencePayload("İade süresi nedir?", "14 gün.", "Ürünü hemen iade edin, süre 14 gündür."),
            new PreferencePayload("Fatura nerede?", "Hesabım sayfasında.", "Bir yerde."),
            new PreferencePayload("Şifre nasıl sıfırlanır?", "Giriş sayfasındaki bağlantıyla.", "Bilmiyorum."));
        Assert.Equal(JudgmentDetails.None, await ws.Judgments.GetAsync(ids[0], Ct));

        await SaveAsync(ws, ids[0], 5, "Doğru ve eksiksiz.");
        await SaveAsync(ws, ids[1], 2, "Reddedilen yanıt daha iyi.", JudgeFlags.RejectedBetter);
        await SaveAsync(ws, ids[2], 3, "Belgede olmayan bilgi var.", JudgeFlags.Ungrounded);

        var details = await ws.Judgments.GetAsync(ids[1], Ct);
        Assert.True(details.IsCurrent);
        Assert.Equal((2, "Reddedilen yanıt daha iyi.", JudgeFlags.RejectedBetter, "qwen2.5:7b"), (details.Judgment!.Score, details.Judgment.Reason, details.Judgment.Flags, details.Judgment.Model));
        Assert.Empty(details.Contradictions);

        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 10, Ct);
        Assert.Equal([5, 2, 3, null], page.Items.Select(r => r.JudgeScore));
        Assert.Equal([ids[1]], await IdsAsync(ws, datasetId, QualityFilter.LowJudgeScore));
        Assert.Equal([ids[3]], await IdsAsync(ws, datasetId, QualityFilter.NotJudged));
        Assert.Equal([ids[2]], await IdsAsync(ws, datasetId, QualityFilter.Ungrounded));
        Assert.Equal([ids[1]], await IdsAsync(ws, datasetId, QualityFilter.PreferenceDoubts));
        Assert.Equal([ids[3]], await ws.Judgments.FindNotJudgedAsync(datasetId, Ct));

        var summary = await ws.Judgments.GetSummaryAsync(datasetId, Ct);
        Assert.Equal((4L, 3L, 0L, 1L, 1L, 1L, 1L), (summary.RecordCount, summary.JudgedCount, summary.OutdatedCount, summary.NotJudgedCount, summary.LowCount, summary.UngroundedCount, summary.PreferenceCount));
        Assert.Equal([0L, 1L, 1L, 0L, 1L], summary.ScoreCounts);
        Assert.Equal(10 / 3.0, summary.Average!.Value, 6);

        // An edit makes the judgment out of date: it is kept, but no longer counts.
        await ws.Commands.UpdatePayloadAsync(ids[1], new PreferencePayload("İade süresi nedir?", "Teslimattan sonra 14 gün.", "Bilmiyorum."), Ct);
        details = await ws.Judgments.GetAsync(ids[1], Ct);
        Assert.False(details.IsCurrent);
        Assert.Equal(2, details.Judgment!.Score);
        Assert.Null((await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 10, Ct)).Items[1].JudgeScore);
        Assert.Empty(await IdsAsync(ws, datasetId, QualityFilter.LowJudgeScore));
        Assert.Equal([ids[1], ids[3]], await ws.Judgments.FindNotJudgedAsync(datasetId, Ct));
        summary = await ws.Judgments.GetSummaryAsync(datasetId, Ct);
        Assert.Equal((2L, 1L, 2L), (summary.JudgedCount, summary.OutdatedCount, summary.NotJudgedCount));

        // Judging again replaces it; archived records are left out of the summary.
        await SaveAsync(ws, ids[1], 4, "Artık iyi.", content: (await ws.Queries.GetAsync(ids[1], Ct))!.ContentHash);
        Assert.True((await ws.Judgments.GetAsync(ids[1], Ct)).IsCurrent);
        await ws.Commands.SetStatusAsync([ids[2]], RecordStatus.Archived, Ct);
        summary = await ws.Judgments.GetSummaryAsync(datasetId, Ct);
        Assert.Equal((3L, 2L, 0L), (summary.RecordCount, summary.JudgedCount, summary.OutdatedCount));
        Assert.Equal(1, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordJudgments WHERE Score = 4"));
        Assert.Equal(0, (await ws.Judgments.GetSummaryAsync(Guid.NewGuid(), Ct)).RecordCount);
    }

    [Fact]
    public async Task Records_that_ask_the_same_thing_are_compared_once_and_contradictions_last_while_both_are_unchanged()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId,
            new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz."),
            new InstructionPayload("İade süresi nedir?", "", "14 gün."),
            new InstructionPayload("kargo ücreti ne kadar", "", "49,90 TL."), // the same question
            new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz."), // the same content
            new InstructionPayload("Kargo ücreti ne kadardır?", "", "Her siparişte 49,90 TL.")); // a near duplicate
        // A variation with the same answer cannot contradict; one with another answer can.
        var source = (await ws.Queries.GetAsync(ids[0], Ct))!;
        var variants = await ws.Commands.AddManyAsync(datasetId,
        [
            new NewRecord(new InstructionPayload("Kargo için ne ödüyorum?", "", "500 TL üzeri ücretsiz."), new RecordLineage(RecordSourceKind.Synthetic, "#1"), VariantOf.For(source)),
            new NewRecord(new InstructionPayload("Gönderim ücreti nedir?", "", "Her zaman ücretli."), new RecordLineage(RecordSourceKind.Synthetic, "#1"), VariantOf.For(source)),
        ], Ct);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);

        // The same question, its near duplicate and the family, not the same content or answer.
        var siblings = await ws.Judgments.FindSiblingsAsync(ids[0], 10, Ct);
        Assert.Equal([3L, 5L, 7L], siblings.Select(s => s.Ordinal));
        Assert.Equal([3L], (await ws.Judgments.FindSiblingsAsync(ids[0], 1, Ct)).Select(s => s.Ordinal));
        Assert.Equal([1L, 4L, 5L], (await ws.Judgments.FindSiblingsAsync(ids[2], 10, Ct)).Select(s => s.Ordinal));

        await SaveConflictAsync(ws, ids[2], ids[0], contradicts: true, "Ücretsiz ile 49,90 TL farklı.");
        await SaveConflictAsync(ws, ids[0], ids[4], contradicts: false, "Biri sınırı söylüyor.");
        await SaveConflictAsync(ws, variants[1], ids[0], contradicts: true, "Her zaman ücretli ile ücretsiz farklı.");

        var details = await ws.Judgments.GetAsync(ids[0], Ct);
        Assert.Null(details.Judgment);
        Assert.Equal([(3L, "Ücretsiz ile 49,90 TL farklı."), (7L, "Her zaman ücretli ile ücretsiz farklı.")], details.Contradictions.Select(c => (c.OtherOrdinal, c.Reason)));
        Assert.Equal([(1L, "Ücretsiz ile 49,90 TL farklı.")], (await ws.Judgments.GetAsync(ids[2], Ct)).Contradictions.Select(c => (c.OtherOrdinal, c.Reason)));
        Assert.Equal([ids[0], ids[2], variants[1]], await IdsAsync(ws, datasetId, QualityFilter.Contradictions));
        Assert.Equal(3, (await ws.Judgments.GetSummaryAsync(datasetId, Ct)).ContradictionCount);

        // Pairs already compared as they are are not offered again; saving a pair again replaces it.
        Assert.Empty(await ws.Judgments.FindSiblingsAsync(ids[0], 10, Ct));
        await SaveConflictAsync(ws, ids[0], variants[1], contradicts: false, "Yeniden bakıldı.");
        Assert.Equal(2, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordConflicts WHERE Contradicts = 0"));
        Assert.Equal([ids[0], ids[2]], await IdsAsync(ws, datasetId, QualityFilter.Contradictions));

        // Archiving the other record settles a contradiction (like copies, the archived one still
        // shows what it contradicts); changing either one settles it too.
        await ws.Commands.SetStatusAsync([ids[2]], RecordStatus.Archived, Ct);
        Assert.Empty((await ws.Judgments.GetAsync(ids[0], Ct)).Contradictions);
        Assert.Equal([ids[2]], await IdsAsync(ws, datasetId, QualityFilter.Contradictions));
        await ws.Commands.SetStatusAsync([ids[2]], RecordStatus.Draft, Ct);
        Assert.Single((await ws.Judgments.GetAsync(ids[0], Ct)).Contradictions);
        await ws.Commands.UpdatePayloadAsync(ids[2], new InstructionPayload("kargo ücreti ne kadar", "", "500 TL altında 49,90 TL."), Ct);
        Assert.Empty((await ws.Judgments.GetAsync(ids[0], Ct)).Contradictions);

        // Deleting a record takes its judgment and comparisons with it.
        await SaveAsync(ws, ids[0], 4, "İyi.");
        await ws.Commands.DeleteAsync([ids[0]], Ct);
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordJudgments"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordConflicts"));
        await Assert.ThrowsAsync<ArgumentException>(() => SaveConflictAsync(ws, ids[2], ids[2], contradicts: true, "Kendisi."));
    }

    private static async Task<List<Guid>> AddAsync(TestWorkspace ws, Guid datasetId, params RecordPayload[] payloads)
    {
        var ids = new List<Guid>();
        foreach (var payload in payloads)
        {
            ids.Add(await ws.Commands.AddAsync(datasetId, payload, RecordLineage.Manual(), Ct));
        }

        return ids;
    }

    private static async Task SaveAsync(TestWorkspace ws, Guid recordId, int score, string reason, JudgeFlags flags = JudgeFlags.None, string? content = null)
    {
        content ??= (await ws.Queries.GetAsync(recordId, Ct))!.ContentHash;
        await ws.Judgments.SaveAsync(new RecordJudgment(recordId, content, score, reason, flags, "qwen2.5:7b", TestWorkspace.Now), Ct);
    }

    private static async Task SaveConflictAsync(TestWorkspace ws, Guid recordId, Guid otherId, bool contradicts, string reason)
    {
        var record = (await ws.Queries.GetAsync(recordId, Ct))!;
        var other = (await ws.Queries.GetAsync(otherId, Ct))!;
        await ws.Judgments.SaveConflictAsync(new RecordConflict(recordId, otherId, record.ContentHash, other.ContentHash, contradicts, reason, "qwen2.5:7b", TestWorkspace.Now), Ct);
    }

    private static async Task<List<Guid>> IdsAsync(TestWorkspace ws, Guid datasetId, QualityFilter filter) =>
        [.. (await ws.Queries.GetPageAsync(datasetId, new RecordFilter(Quality: filter), null, 50, Ct)).Items.Select(r => r.Id)];
}
