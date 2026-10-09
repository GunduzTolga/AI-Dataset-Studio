using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Analysis;

/// <summary>
/// Near-duplicate questions (M10): found by the grouping pass after analysis, read in SQL with
/// status and freshness, cleaned up, and kept in one split part.
/// </summary>
public sealed class NearDuplicateAnalysisTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RecordPayload[] Kargo =>
    [
        new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri siparişlerde ücretsiz."),
        new InstructionPayload("Mesai saatleri nedir?", "", "09.00 – 18.00."),
        new InstructionPayload("Kargo ücreti ne kadardır?", "", "Ücretsiz kargo sınırı 500 TL."), // near the first (0.85)
        new InstructionPayload("kargo ücreti ne kadar", "", "49,90 TL."), // the first question exactly: a conflicting answer
    ];

    [Fact]
    public async Task Near_questions_are_found_with_the_number_of_the_other_record()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Kargo);

        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);

        Assert.Equal("3", await NearDetailAsync(ws, ids[0])); // #4 asks exactly the same: not near
        Assert.Null(await NearDetailAsync(ws, ids[1]));
        Assert.Equal("1", await NearDetailAsync(ws, ids[2]));
        Assert.Equal("3", await NearDetailAsync(ws, ids[3]));
        Assert.Contains((await ws.Analysis.GetRecordQualityAsync(ids[3], Ct))!.Issues, i => i.Rule == QualityRule.ConflictingAnswer);

        var summary = await ws.Analysis.GetSummaryAsync(datasetId, Ct);
        Assert.Equal(3, summary.IssueCounts[QualityRule.NearDuplicate]);
        var near = await ws.Queries.GetPageAsync(datasetId, new RecordFilter(Quality: QualityFilter.NearDuplicates), null, 10, Ct);
        Assert.Equal([ids[0], ids[2], ids[3]], near.Items.Select(r => r.Id));
        Assert.Equal(90, near.Items[1].Score);

        // Clean-up keeps the first record of the group.
        Assert.Equal([ids[2], ids[3]], await ws.Analysis.FindAsync(datasetId, QualityRule.NearDuplicate, Ct));
    }

    [Fact]
    public async Task Archiving_settles_a_group_and_edits_group_again()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Kargo);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);

        // Archived records do not make others near-duplicates (no new pass needed).
        await ws.Commands.SetStatusAsync([ids[2]], RecordStatus.Archived, Ct);
        Assert.Null(await NearDetailAsync(ws, ids[0]));
        Assert.False((await ws.Analysis.GetSummaryAsync(datasetId, Ct)).IssueCounts.ContainsKey(QualityRule.NearDuplicate));
        Assert.Empty(await ws.Analysis.FindAsync(datasetId, QualityRule.NearDuplicate, Ct));
        await ws.Commands.SetStatusAsync([ids[2]], RecordStatus.Draft, Ct);
        Assert.Equal("3", await NearDetailAsync(ws, ids[0]));

        // An edit takes the record out of its group at once (its analysis no longer matches)…
        await ws.Commands.UpdatePayloadAsync(ids[2], new InstructionPayload("İade süresi kaç gün sürüyor?", "", "14 gün."), Ct);
        await ws.Analysis.AnalyzeRecordsAsync([ids[2]], Ct);
        Assert.Null(await NearDetailAsync(ws, ids[0]));
        Assert.Null(await NearDetailAsync(ws, ids[2]));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Null(await NearDetailAsync(ws, ids[0]));

        // …and the dataset's analysis groups it again.
        await ws.Commands.UpdatePayloadAsync(ids[1], new InstructionPayload("İade süresi kaç gün sürüyor ki?", "", "14 gün."), Ct);
        await ws.Analysis.AnalyzeRecordsAsync([ids[1]], Ct);
        Assert.Null(await NearDetailAsync(ws, ids[1]));
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Equal("3", await NearDetailAsync(ws, ids[1]));
        Assert.Equal("2", await NearDetailAsync(ws, ids[2]));

        // Deleting the other record settles it too.
        await ws.Commands.DeleteAsync([ids[1]], Ct);
        Assert.Null(await NearDetailAsync(ws, ids[2]));
    }

    [Fact]
    public async Task Preference_pairs_have_no_near_duplicates()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Preference, Ct);
        await AddAsync(ws, datasetId,
            new PreferencePayload("Kargo ücreti ne kadar?", "500 TL üzeri ücretsiz.", "Bilmiyorum."),
            new PreferencePayload("Kargo ücreti ne kadardır?", "Ücretsiz.", "Söyleyemem."));

        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);

        Assert.Empty((await ws.Analysis.GetSummaryAsync(datasetId, Ct)).IssueCounts);
    }

    [Fact]
    public async Task Near_questions_go_to_one_split_part_also_in_a_version()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var random = new Random(3);
        string[] words = ["sipariş", "kargo", "iade", "fatura", "ödeme", "hesap", "şifre", "adres", "mağaza", "ürün", "teslimat", "kampanya"];
        var questions = Enumerable.Range(0, 60)
            .Select(i => $"{i}: " + string.Join(' ', Enumerable.Range(0, 12).Select(_ => words[random.Next(words.Length)])))
            .ToList();
        // Every question twice: as it is and with a short word added (near, not the same).
        await ws.Commands.AddRangeAsync(datasetId, questions.SelectMany(q => new[]
        {
            new NewRecord(new InstructionPayload(q, "", "Birinci yanıt."), RecordLineage.Manual()),
            new NewRecord(new InstructionPayload(q + " mi", "", "İkinci yanıt."), RecordLineage.Manual()),
        }), cancellationToken: Ct);
        var options = new ExportOptions(ExportFormat.Alpaca, [RecordStatus.Draft], Split: SplitOptions.Standard);

        var result = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "split"), cancellationToken: Ct);

        AssertPairsTogether(result, questions);
        var summary = await ws.Analysis.GetSummaryAsync(datasetId, Ct);
        Assert.Equal(120, summary.IssueCounts[QualityRule.NearDuplicate]);

        // A version keeps the groups: its export splits the same even after the records change.
        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);
        var ids = (await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 200, Ct)).Items.Select(r => r.Id).ToList();
        foreach (var id in ids.Where((_, i) => i % 2 == 1))
        {
            await ws.Commands.UpdatePayloadAsync(id, new InstructionPayload($"Bambaşka bir soru {id:N}", "", "İkinci yanıt."), Ct);
        }

        var frozen = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "frozen"), versionId: version.Id, cancellationToken: Ct);
        Assert.Equal(
            result.Files.Select(f => File.ReadAllText(f.Path)),
            frozen.Files.Select(f => File.ReadAllText(f.Path)));
    }

    [Fact]
    public async Task Variations_are_not_near_duplicates_of_their_family_and_clean_up_keeps_them()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        const string question = "Siparişim kargoya verildikten sonra teslimat kaç iş günü içinde adresime ulaşır";
        var root = await ws.Commands.AddAsync(datasetId, new InstructionPayload(question, "", "İki iş günü."), RecordLineage.Manual(), Ct);
        var other = await ws.Commands.AddAsync(datasetId, new InstructionPayload("Mesai saatleri nedir?", "", "09.00 – 18.00."), RecordLineage.Manual(), Ct);
        var variation = await AddVariationAsync(ws, datasetId, root, question + " mi");

        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Null(await NearDetailAsync(ws, root));
        Assert.Null(await NearDetailAsync(ws, variation));

        // Another record near both: near the family, not the family near itself.
        await ws.Commands.UpdatePayloadAsync(other, new InstructionPayload(question + " mu", "", "Üç iş günü."), Ct);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        Assert.Equal("2", await NearDetailAsync(ws, root));
        Assert.Equal("2", await NearDetailAsync(ws, variation));
        Assert.Equal("1", await NearDetailAsync(ws, other));

        // Clean-up keeps the first record with its variations.
        Assert.Equal([other], await ws.Analysis.FindAsync(datasetId, QualityRule.NearDuplicate, Ct));
    }

    [Fact]
    public async Task A_record_and_its_variations_go_to_one_split_part_also_in_a_version()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var random = new Random(5);
        string[] words = ["sipariş", "kargo", "iade", "fatura", "ödeme", "hesap", "şifre", "adres", "mağaza", "ürün", "teslimat", "kampanya"];
        string Question(int i) => $"{i}: " + string.Join(' ', Enumerable.Range(0, 12).Select(_ => words[random.Next(words.Length)]));

        // Variations ask in other words, so only their family keeps them with the first record.
        var sizes = new List<int>();
        for (var i = 0; i < 60; i++)
        {
            var root = await ws.Commands.AddAsync(datasetId, new InstructionPayload(Question(i), "", $"Yanıt {i}."), RecordLineage.Manual(), Ct);
            var variation = await AddVariationAsync(ws, datasetId, root, Question(i));
            if (i % 3 == 0)
            {
                await AddVariationAsync(ws, datasetId, variation, Question(i));
            }

            sizes.Add(i % 3 == 0 ? 3 : 2);
        }

        var options = new ExportOptions(ExportFormat.Alpaca, [RecordStatus.Draft], Split: SplitOptions.Standard);
        var result = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "split"), cancellationToken: Ct);
        AssertFamiliesTogether(result, sizes);

        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);
        var frozen = await ws.Export.ExportAsync(datasetId, options, Path.Combine(ws.Directory, "frozen"), versionId: version.Id, cancellationToken: Ct);
        Assert.Equal(result.Files.Select(f => File.ReadAllText(f.Path)), frozen.Files.Select(f => File.ReadAllText(f.Path)));

        // A stratified split keeps families together too.
        var stratified = await ws.Export.ExportAsync(
            datasetId,
            options with { Split = SplitOptions.Standard with { StratifyBy = SplitOptions.AnyTag } },
            Path.Combine(ws.Directory, "stratified"),
            cancellationToken: Ct);
        AssertFamiliesTogether(stratified, sizes);
    }

    private static void AssertFamiliesTogether(ExportResult result, List<int> sizes)
    {
        var lines = result.Files.Select(f => File.ReadAllLines(f.Path)).ToList();
        Assert.Equal(sizes.Sum(), lines.Sum(l => l.Length));
        Assert.True(lines.Count(l => l.Length > 0) > 1);
        for (var i = 0; i < sizes.Count; i++)
        {
            var answer = $"\"Yanıt {i}.\"";
            var file = Assert.Single(lines, file => file.Any(l => l.Contains(answer, StringComparison.Ordinal)));
            Assert.Equal(sizes[i], file.Count(l => l.Contains(answer, StringComparison.Ordinal)));
        }
    }

    private static void AssertPairsTogether(ExportResult result, IEnumerable<string> questions)
    {
        var lines = result.Files.Select(f => File.ReadAllLines(f.Path)).ToList();
        foreach (var question in questions)
        {
            // Both records of the pair start with the question; a single file holds them.
            var prefix = $"{{\"instruction\":\"{question}";
            var files = lines.Where(file => file.Any(l => l.StartsWith(prefix, StringComparison.Ordinal))).ToList();
            var file = Assert.Single(files);
            Assert.Equal(2, file.Count(l => l.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }

    private static async Task<string?> NearDetailAsync(TestWorkspace ws, Guid recordId) =>
        (await ws.Analysis.GetRecordQualityAsync(recordId, Ct))!.Issues.SingleOrDefault(i => i.Rule == QualityRule.NearDuplicate)?.Detail;

    private static async Task<Guid> AddVariationAsync(TestWorkspace ws, Guid datasetId, Guid sourceId, string question)
    {
        var source = (await ws.Queries.GetAsync(sourceId, Ct))!;
        var payload = new InstructionPayload(question, "", ((InstructionPayload)source.Payload).Output);
        var lineage = new RecordLineage(RecordSourceKind.Synthetic, "#1", GeneratedByModel: "qwen2.5:7b");
        return Assert.Single(await ws.Commands.AddManyAsync(datasetId, [new NewRecord(payload, lineage, VariantOf.For(source))], Ct));
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
}
