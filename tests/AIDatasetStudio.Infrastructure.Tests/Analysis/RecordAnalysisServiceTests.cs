using System.Diagnostics;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Analysis;

public sealed class RecordAnalysisServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Tokenizers.O200kBase)]
    [InlineData(Tokenizers.Cl100kBase)]
    public void Openai_encodings_count_offline(string tokenizer)
    {
        var counter = TestWorkspace.Tokenizers.Get(tokenizer);

        Assert.Equal(4, counter.CountTokens("Hello, world!"));
        Assert.Equal(0, counter.CountTokens(string.Empty));
        Assert.InRange(counter.CountTokens("Yıllık izin hakkım kaç gün?"), 5, 15);
    }

    [Fact]
    public void Unknown_tokenizers_are_refused()
    {
        Assert.Throws<ArgumentException>(() => TestWorkspace.Tokenizers.Get("llama3"));
    }

    [Fact]
    public async Task Analysis_stores_tokens_and_issues_and_the_list_reads_them()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId,
            new InstructionPayload("Başkent neresi?", "", "Türkiye'nin başkenti Ankara'dır."),
            new InstructionPayload("Başkent neresi?", "", "Türkiye'nin başkenti Ankara'dır."), // duplicate
            new InstructionPayload("Başkent neresi?", "", "İstanbul."),                        // conflicting answer
            new InstructionPayload("Bana yazın", "", "ali@firma.com"),                         // personal data
            new InstructionPayload("Boş cevap", "", ""));                                      // error

        Assert.Equal(5, await ws.Analysis.CountPendingAsync(datasetId, Ct));
        var progress = new List<int>();
        Assert.Equal(5, await ws.Analysis.AnalyzeAsync(datasetId, new InlineProgress(progress.Add), Ct));
        Assert.Equal([5], progress);
        Assert.Equal(0, await ws.Analysis.CountPendingAsync(datasetId, Ct));
        Assert.Equal(0, await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct));

        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.All(page.Items, row => Assert.True(row.TotalTokens > 0 || row.Ordinal == 5));
        Assert.Equal([80, 80, 90, 90, 60], page.Items.Select(row => row.Score)); // dup+conflict, dup+conflict, conflict, PII, empty

        var summary = await ws.Analysis.GetSummaryAsync(datasetId, Ct);
        Assert.Equal((5L, 5L), (summary.RecordCount, summary.AnalyzedCount));
        Assert.Equal(80, summary.Score);
        Assert.Equal(2, summary.IssueCounts[QualityRule.Duplicate]);
        Assert.Equal(3, summary.IssueCounts[QualityRule.ConflictingAnswer]);
        Assert.Equal(1, summary.IssueCounts[QualityRule.PersonalData]);
        Assert.Equal(1, summary.IssueCounts[QualityRule.EmptyContent]);
        Assert.False(summary.IssueCounts.ContainsKey(QualityRule.OverContextLimit));
        Assert.Equal(5, summary.Histogram[0].Count); // all under 128 tokens
        Assert.Equal(Tokenizers.HistogramBounds.Count + 1, summary.Histogram.Count);
        Assert.Equal(page.Items.Sum(r => r.TotalTokens ?? 0), summary.TotalTokens);

        var quality = await ws.Analysis.GetRecordQualityAsync(ids[1], Ct);
        Assert.Equal([QualityRule.Duplicate, QualityRule.ConflictingAnswer], quality!.Issues.Select(i => i.Rule));
        Assert.Equal(80, quality.Score);

        // Clean-up keeps the first copy of each group.
        Assert.Equal([ids[1]], await ws.Analysis.FindAsync(datasetId, QualityRule.Duplicate, Ct));
        Assert.Equal([ids[0], ids[1], ids[2]], await ws.Analysis.FindAsync(datasetId, QualityRule.ConflictingAnswer, Ct));
        Assert.Equal([ids[3]], await ws.Analysis.FindAsync(datasetId, QualityRule.PersonalData, Ct));
    }

    [Fact]
    public async Task Quality_filters_narrow_lists_and_counts()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId,
            new InstructionPayload("a", "", "aynı"),
            new InstructionPayload("b", "", "aynı"),
            new InstructionPayload("c", "", "0532 123 45 67"),
            new InstructionPayload("d", "", ""),
            new InstructionPayload("e", "", "temiz bir cevap"));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        await ws.Commands.SetStatusAsync([ids[3]], RecordStatus.Approved, Ct);

        async Task<long> Count(QualityFilter quality) => await ws.Queries.CountAsync(datasetId, new RecordFilter(Quality: quality), Ct);

        Assert.Equal(2, await Count(QualityFilter.Issues)); // personal data, empty answer
        Assert.Equal(1, await Count(QualityFilter.Errors));
        Assert.Equal(0, await Count(QualityFilter.Duplicates));
        Assert.Equal(1, await Count(QualityFilter.PersonalData));
        Assert.Equal(0, await Count(QualityFilter.OverContextLimit));
        var byStatus = await ws.Queries.CountByStatusAsync(datasetId, new RecordFilter(Quality: QualityFilter.Issues), Ct);
        Assert.Equal((1L, 1L), (byStatus[RecordStatus.Draft], byStatus[RecordStatus.Approved]));
        var page = await ws.Queries.GetPageAsync(datasetId, new RecordFilter(Quality: QualityFilter.Errors), after: null, pageSize: 10, Ct);
        Assert.Equal([ids[3]], page.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Archived_records_leave_the_figures_and_settle_cross_record_issues()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId,
            new InstructionPayload("Başkent neresi?", "", "Ankara."),
            new InstructionPayload("Başkent neresi?", "", "Ankara."),   // duplicate
            new InstructionPayload("Başkent neresi?", "", "İstanbul."), // conflicting answer
            new InstructionPayload("Bana yazın", "", "ali@firma.com"));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Equal([ids[1]], await ws.Analysis.FindAsync(datasetId, QualityRule.Duplicate, Ct));

        await ws.Commands.SetStatusAsync([ids[1], ids[2], ids[3]], RecordStatus.Archived, Ct);

        var summary = await ws.Analysis.GetSummaryAsync(datasetId, Ct);
        Assert.Equal((4L, 4L), (summary.RecordCount, summary.AnalyzedCount));
        Assert.Equal(100, summary.Score);
        Assert.Empty(summary.IssueCounts);
        Assert.Equal(1, summary.Histogram.Sum(b => b.Count));
        Assert.Empty(await ws.Analysis.FindAsync(datasetId, QualityRule.Duplicate, Ct));
        Assert.Empty(await ws.Analysis.FindAsync(datasetId, QualityRule.PersonalData, Ct)); // already archived
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal(100, page.Items[0].Score);
        Assert.Equal(summary.TotalTokens, (long)page.Items[0].TotalTokens!);
        // An archived copy is still a copy of a record that stays (but no longer conflicts with the archived answer).
        Assert.Equal(90, page.Items[1].Score);
        var copies = await ws.Queries.GetPageAsync(datasetId, new RecordFilter(Quality: QualityFilter.Duplicates), after: null, pageSize: 10, Ct);
        Assert.Equal([ids[1]], copies.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Results_go_stale_with_edits_and_tokenizer_changes_but_not_with_context_length()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId,
            new InstructionPayload("Kısa soru", "", string.Join(' ', Enumerable.Repeat("uzun bir cevap", 120))),
            new InstructionPayload("Diğer soru", "", "Kısa cevap"));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);

        await ws.Commands.UpdatePayloadAsync(ids[1], new InstructionPayload("Diğer soru", "", "Yeni cevap"), Ct);
        Assert.Equal(1, await ws.Analysis.CountPendingAsync(datasetId, Ct));
        var quality = await ws.Analysis.GetRecordQualityAsync(ids[1], Ct); // analyses on demand
        Assert.True(quality!.IsAnalyzed);
        Assert.Equal(0, await ws.Analysis.CountPendingAsync(datasetId, Ct));

        await ws.Catalog.SetTokenSettingsAsync(datasetId, Tokenizers.O200kBase, 256, Ct);
        Assert.Equal(0, await ws.Analysis.CountPendingAsync(datasetId, Ct));
        Assert.Equal([ids[0]], await ws.Analysis.FindAsync(datasetId, QualityRule.OverContextLimit, Ct));
        var over = await ws.Analysis.GetRecordQualityAsync(ids[0], Ct);
        Assert.Contains(over!.Issues, i => i.Rule == QualityRule.OverContextLimit && i.Severity == IssueSeverity.Error);
        Assert.Equal(1, (await ws.Analysis.GetSummaryAsync(datasetId, Ct)).OverLimitCount);
        Assert.True((await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 10, Ct)).Items[0].IsOverContextLimit);

        await ws.Catalog.SetTokenSettingsAsync(datasetId, Tokenizers.Cl100kBase, 256, Ct);
        Assert.Equal(2, await ws.Analysis.CountPendingAsync(datasetId, Ct));
        Assert.Null((await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 10, Ct)).Items[0].TotalTokens);
        await Assert.ThrowsAsync<Domain.Common.DomainException>(() => ws.Catalog.SetTokenSettingsAsync(datasetId, "llama3", 4096, Ct));
        await Assert.ThrowsAsync<Domain.Common.DomainException>(() => ws.Catalog.SetTokenSettingsAsync(datasetId, Tokenizers.O200kBase, 10, Ct));
    }

    [Fact]
    public async Task Custom_terms_are_stored_and_reanalysed()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, new InstructionPayload("Kartal projesi ne durumda?", "", "Planlandığı gibi."));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Empty(await ws.Analysis.FindAsync(datasetId, QualityRule.PersonalData, Ct));

        await ws.Analysis.SetCustomTermsAsync([" Kartal ", "kartal", ""], Ct);

        Assert.Equal(["Kartal"], await ws.Analysis.GetCustomTermsAsync(Ct));
        Assert.Equal(1, await ws.Analysis.CountPendingAsync(datasetId, Ct));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Equal(ids, await ws.Analysis.FindAsync(datasetId, QualityRule.PersonalData, Ct));
    }

    [Fact]
    public async Task Analyses_are_deleted_with_their_records()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, new InstructionPayload("a", "", "b"), new InstructionPayload("c", "", "d"));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);

        await ws.Commands.DeleteAsync([ids[0]], Ct);
        Assert.Equal(1, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordAnalyses"));

        await ws.Catalog.DeleteDatasetAsync(datasetId, Ct);
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordAnalyses"));
        Assert.Null(await ws.Analysis.GetRecordQualityAsync(ids[1], Ct));
    }

    [Fact]
    public async Task Analyses_ten_thousand_records()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        await ws.Commands.AddRangeAsync(datasetId, Enumerable.Range(0, 10_000).Select(i => new NewRecord(
            new ChatPayload("Kısa ve nazik yanıt ver.", [
                new ChatMessage(ChatRole.User, $"Soru {i}: Siparişimin kargo durumunu nasıl öğrenebilirim?"),
                new ChatMessage(ChatRole.Assistant, $"Yanıt {i}: Hesabım > Siparişlerim sayfasından takip numaranızla kargo durumunu görebilirsiniz."),
            ]),
            RecordLineage.Manual())), cancellationToken: Ct);
        var stopwatch = Stopwatch.StartNew();

        var analyzed = await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        var analyzeTime = stopwatch.Elapsed;
        var summary = await ws.Analysis.GetSummaryAsync(datasetId, Ct);
        var page = await ws.Queries.GetPageAsync(datasetId, new RecordFilter(Quality: QualityFilter.Issues), null, 200, Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"analyze {analyzeTime.TotalMilliseconds:F0} ms, summary+page {(stopwatch.Elapsed - analyzeTime).TotalMilliseconds:F0} ms");
        Assert.Equal(10_000, analyzed);
        Assert.Equal(10_000, summary.AnalyzedCount);
        // The questions differ only in their number: near-duplicates, and nothing else is wrong.
        Assert.Equal(90, summary.Score);
        Assert.Equal(new Dictionary<QualityRule, long> { [QualityRule.NearDuplicate] = 10_000 }, summary.IssueCounts);
        Assert.Equal(200, page.Items.Count);
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

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
