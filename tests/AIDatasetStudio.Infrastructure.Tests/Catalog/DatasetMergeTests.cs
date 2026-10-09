using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Catalog;

/// <summary>
/// Merging datasets from several projects into a new one (M21, ADR-043).
/// </summary>
public sealed class DatasetMergeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Records_are_copied_whole_in_order_without_copies_or_archived_ones_and_the_sources_stay()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var sales = await ws.Catalog.CreateProjectAsync("Satış", cancellationToken: Ct);
        var support = await ws.Catalog.CreateProjectAsync("Destek", cancellationToken: Ct);
        var orders = await ws.Catalog.CreateDatasetAsync(sales, "Sipariş SSS", DatasetType.Instruction, Ct);
        var returns = await ws.Catalog.CreateDatasetAsync(support, "İade SSS", DatasetType.Instruction, Ct);
        await ws.Catalog.SetTokenSettingsAsync(orders, "cl100k_base", 8192, Ct);
        await ws.Catalog.SetAIGuidanceAsync(orders, "Resmî Türkçe yaz.", Ct);
        var tag = await ws.Tags.CreateAsync("kargo", Ct);

        var cargo = await Add(ws, orders, "Kargo ne zaman gelir?", "1-3 iş günü.", new RecordLineage(RecordSourceKind.FileImport, "sss.csv#row=2"));
        await ws.Commands.SetStatusAsync([cargo], RecordStatus.Approved, Ct);
        await ws.Commands.SetTagAsync([cargo], tag.Id, present: true, Ct);
        var old = await Add(ws, orders, "Eski kampanya?", "Bitti.");
        await ws.Commands.SetStatusAsync([old], RecordStatus.Archived, Ct);
        await Add(ws, returns, "İade süresi?", "14 gün.");
        await Add(ws, returns, "Kargo ne zaman gelir?", "1-3 iş günü."); // the same as in orders

        var reports = new List<DatasetMergeProgress>();
        var result = await ws.Merge.MergeAsync(
            new DatasetMergeRequest([orders, returns], support, "Tüm SSS"), new InlineProgress<DatasetMergeProgress>(reports.Add), Ct);

        Assert.Equal((2L, 1L, 1L), (result.Copied, result.Duplicates, result.Archived));
        Assert.Equal(new DatasetMergeProgress(4, 4), reports[^1]);
        var merged = (await ws.Catalog.GetProjectsAsync(Ct)).Single(p => p.Id == support).Datasets.Single(d => d.Id == result.DatasetId);
        Assert.Equal(("Tüm SSS", DatasetType.Instruction, 2L, "cl100k_base", 8192, "Resmî Türkçe yaz."), (merged.Name, merged.Type, merged.RecordCount, merged.Tokenizer, merged.ContextLength, merged.AIGuidance));

        var items = (await ws.Queries.GetPageAsync(result.DatasetId, RecordFilter.None, after: null, pageSize: 10, Ct)).Items;
        var first = (await ws.Queries.GetAsync(items[0].Id, Ct))!;
        Assert.NotEqual(cargo, first.Id);
        Assert.Equal(new InstructionPayload("Kargo ne zaman gelir?", "", "1-3 iş günü."), first.Payload);
        Assert.Equal((RecordStatus.Approved, "sss.csv#row=2", 1L), (first.Status, first.Lineage.SourceReference, first.Ordinal));
        Assert.Equal([tag.Id], first.TagIds);
        Assert.Equal("İade süresi?", ((InstructionPayload)(await ws.Queries.GetAsync(items[1].Id, Ct))!.Payload).Instruction);

        // The copies are found by search, and the sources are as they were.
        Assert.Single((await ws.Queries.GetPageAsync(result.DatasetId, new RecordFilter(SearchText: "iade"), after: null, pageSize: 10, Ct)).Items);
        Assert.Equal(2, (await ws.Queries.GetPageAsync(orders, RecordFilter.None, after: null, pageSize: 10, Ct)).Items.Count);
        Assert.Equal(2, (await ws.Queries.GetPageAsync(returns, RecordFilter.None, after: null, pageSize: 10, Ct)).Items.Count);
    }

    [Fact]
    public async Task Variations_keep_their_family_and_everything_can_be_taken_as_drafts()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var project = await ws.Catalog.CreateProjectAsync("Destek", cancellationToken: Ct);
        var a = await ws.Catalog.CreateDatasetAsync(project, "A", DatasetType.Instruction, Ct);
        var b = await ws.Catalog.CreateDatasetAsync(project, "B", DatasetType.Instruction, Ct);
        await Add(ws, a, "Önce bu", "1");
        var parent = await Add(ws, b, "Şifremi unuttum", "Bağlantıya tıklayın.");
        await ws.Commands.SetStatusAsync([parent], RecordStatus.Approved, Ct);
        var parentDetails = (await ws.Queries.GetAsync(parent, Ct))!;
        var variant = (await ws.Commands.AddManyAsync(b, [new NewRecord(new InstructionPayload("Parolamı nasıl sıfırlarım?", "", "Bağlantıya tıklayın."), new RecordLineage(RecordSourceKind.Synthetic, GeneratedByModel: "qwen2.5:7b"), VariantOf.For(parentDetails))], Ct))[0];
        var archivedParent = await Add(ws, b, "Eski soru", "Eski.");
        var archivedDetails = (await ws.Queries.GetAsync(archivedParent, Ct))!;
        await ws.Commands.AddManyAsync(b, [new NewRecord(new InstructionPayload("Eski soru başka türlü", "", "Eski."), new RecordLineage(RecordSourceKind.Synthetic, GeneratedByModel: "qwen2.5:7b"), VariantOf.For(archivedDetails))], Ct);
        await ws.Commands.SetStatusAsync([archivedParent], RecordStatus.Archived, Ct);

        var result = await ws.Merge.MergeAsync(new DatasetMergeRequest([a, b], project, "A + B", AsDrafts: true), cancellationToken: Ct);

        var copies = new List<RecordDetails>();
        foreach (var item in (await ws.Queries.GetPageAsync(result.DatasetId, RecordFilter.None, after: null, pageSize: 10, Ct)).Items)
        {
            copies.Add((await ws.Queries.GetAsync(item.Id, Ct))!);
        }

        Assert.Equal(["Önce bu", "Şifremi unuttum", "Parolamı nasıl sıfırlarım?", "Eski soru başka türlü"], copies.Select(c => ((InstructionPayload)c.Payload).Instruction));
        Assert.All(copies, c => Assert.Equal(RecordStatus.Draft, c.Status));

        // The variation points at its family's copy; the archived family's first record was left out.
        Assert.Equal(new VariantOf(copies[1].Id, 2), VariantOf.FromMetadata(copies[2].MetadataJson));
        Assert.Null(VariantOf.FromMetadata(copies[3].MetadataJson));
        Assert.NotEqual(variant, copies[2].Id);
    }

    [Fact]
    public async Task Datasets_of_different_types_are_refused_and_a_cancelled_merge_leaves_nothing()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var project = await ws.Catalog.CreateProjectAsync("Destek", cancellationToken: Ct);
        var instructions = await ws.Catalog.CreateDatasetAsync(project, "Talimat", DatasetType.Instruction, Ct);
        var chats = await ws.Catalog.CreateDatasetAsync(project, "Sohbet", DatasetType.Chat, Ct);
        await Add(ws, instructions, "Soru", "Cevap");

        await Assert.ThrowsAsync<ArgumentException>(() => ws.Merge.MergeAsync(new DatasetMergeRequest([instructions, chats], project, "Karışık"), cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ws.Merge.MergeAsync(new DatasetMergeRequest([], project, "Boş"), cancellationToken: Ct));

        // Stopped once the new dataset exists: it is removed again.
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ws.Merge.MergeAsync(
            new DatasetMergeRequest([instructions], project, "Yarım"), new InlineProgress<DatasetMergeProgress>(_ => cancelled.Cancel()), cancelled.Token));

        // Created at the same (fixed) time, the datasets come in no particular order.
        Assert.Equal(["Sohbet", "Talimat"], (await ws.Catalog.GetProjectsAsync(Ct)).Single().Datasets.Select(d => d.Name).Order(StringComparer.Ordinal));
    }

    private static async Task<Guid> Add(TestWorkspace ws, Guid datasetId, string question, string answer, RecordLineage? lineage = null) =>
        await ws.Commands.AddAsync(datasetId, new InstructionPayload(question, "", answer), lineage ?? RecordLineage.Manual(), Ct);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
