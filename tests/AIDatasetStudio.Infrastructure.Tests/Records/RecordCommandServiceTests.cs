using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Tests.Records;

public sealed class RecordCommandServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Added_records_are_drafts_appended_in_order()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);

        var first = await ws.Commands.AddAsync(datasetId, new InstructionPayload("first", "", ""), RecordLineage.Manual("ayse"), Ct);
        var second = await ws.Commands.AddAsync(datasetId, new InstructionPayload("second", "", ""), RecordLineage.Manual(), Ct);

        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal([first, second], page.Items.Select(r => r.Id));
        Assert.Equal([1L, 2L], page.Items.Select(r => r.Ordinal));
        Assert.All(page.Items, r => Assert.Equal(RecordStatus.Draft, r.Status));
        Assert.Equal("first", page.Items[0].Preview);
    }

    [Fact]
    public async Task Adding_checks_the_dataset()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var chatDataset = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);

        await Assert.ThrowsAsync<DomainException>(() =>
            ws.Commands.AddAsync(chatDataset, new InstructionPayload("i", "", ""), RecordLineage.Manual(), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            ws.Commands.AddAsync(Guid.NewGuid(), new InstructionPayload("i", "", ""), RecordLineage.Manual(), Ct));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM DatasetRecords"));
    }

    [Fact]
    public async Task Bulk_add_writes_rows_that_ef_core_reads_back_identically()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        await ws.Commands.AddAsync(datasetId, new ChatPayload(null, [new ChatMessage(ChatRole.User, "existing")]), RecordLineage.Manual(), Ct);
        var payload = new ChatPayload("Sen bir İK asistanısın.", [
            new ChatMessage(ChatRole.User, "İzin?"),
            new ChatMessage(ChatRole.Assistant, "14 gün."),
        ]);
        var lineage = new RecordLineage(RecordSourceKind.FileImport, "hr.jsonl#line=7", "ayse", "gpt-4o");

        var added = await ws.Commands.AddRangeAsync(datasetId, [new NewRecord(payload, lineage)], cancellationToken: Ct);

        Assert.Equal(1, added);
        await using var context = ws.Workspace.CreateDbContext();
        var record = await context.Records.SingleAsync(r => r.Ordinal == 2, Ct);
        Assert.Equal(payload, record.Payload);
        Assert.Equal(payload.ComputeContentHash(), record.ContentHash);
        Assert.Equal("İzin?", record.Preview);
        Assert.Equal(lineage, record.Lineage);
        Assert.Equal(DatasetType.Chat, record.Type);
        Assert.Equal(RecordStatus.Draft, record.Status);
        Assert.Equal(TestWorkspace.Now, record.CreatedAt);
        Assert.Equal("{}", record.MetadataJson);
    }

    [Fact]
    public async Task Bulk_add_handles_ten_thousand_records_and_reports_progress()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var reports = new List<int>();
        var progress = new SynchronousProgress(reports.Add);

        var added = await ws.Commands.AddRangeAsync(
            datasetId,
            Enumerable.Range(1, 10_000).Select(i => new NewRecord(new InstructionPayload($"Soru {i}", "", $"Cevap {i}"), RecordLineage.Manual())),
            progress,
            Ct);

        Assert.Equal(10_000, added);
        Assert.Equal(10_000, await ws.Queries.CountAsync(datasetId, RecordFilter.None, Ct));
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "cevap 9999"), Ct));
        Assert.Equal(10_000, reports[^1]);
        Assert.True(reports.Count >= 10);
    }

    [Fact]
    public async Task Cancelled_bulk_add_writes_nothing()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        IEnumerable<NewRecord> Records()
        {
            for (var i = 0; ; i++)
            {
                if (i == 500)
                {
                    cancellation.Cancel();
                }

                yield return new NewRecord(new InstructionPayload($"r{i}", "", ""), RecordLineage.Manual());
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ws.Commands.AddRangeAsync(datasetId, Records(), cancellationToken: cancellation.Token));

        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM DatasetRecords"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordSearchKeys"));
    }

    [Fact]
    public async Task Updating_content_reindexes_and_sends_the_record_to_review()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var id = await ws.Commands.AddAsync(datasetId, new InstructionPayload("eski metin", "", ""), RecordLineage.Manual(), Ct);
        await ws.Commands.SetStatusAsync([id], RecordStatus.Approved, Ct);

        var changed = await ws.Commands.UpdatePayloadAsync(id, new InstructionPayload("yeni metin", "", ""), Ct);
        var unchanged = await ws.Commands.UpdatePayloadAsync(id, new InstructionPayload("yeni metin", "", ""), Ct);

        Assert.True(changed);
        Assert.False(unchanged);
        Assert.Equal(0, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "eski"), Ct));
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "yeni"), Ct));
        var details = await ws.Queries.GetAsync(id, Ct);
        Assert.Equal(RecordStatus.InReview, details!.Status);
        Assert.Equal(1, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordSearchKeys"));
    }

    [Fact]
    public async Task Status_changes_skip_transitions_the_lifecycle_forbids()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var draft = await ws.Commands.AddAsync(datasetId, new InstructionPayload("a", "", ""), RecordLineage.Manual(), Ct);
        var archived = await ws.Commands.AddAsync(datasetId, new InstructionPayload("b", "", ""), RecordLineage.Manual(), Ct);
        await ws.Commands.SetStatusAsync([archived], RecordStatus.Archived, Ct);

        var result = await ws.Commands.SetStatusAsync([draft, archived, Guid.NewGuid()], RecordStatus.Approved, Ct);

        Assert.Equal([new RecordStatusUpdate(draft, RecordStatus.Draft, RecordStatus.Approved)], result.Changes);
        Assert.Equal((1, 2), (result.Changed, result.Skipped));
        Assert.Equal(RecordStatus.Approved, (await ws.Queries.GetAsync(draft, Ct))!.Status);
        Assert.Equal(RecordStatus.Archived, (await ws.Queries.GetAsync(archived, Ct))!.Status);
    }

    [Fact]
    public async Task Deleted_records_leave_the_search_index()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var keep = await ws.Commands.AddAsync(datasetId, new InstructionPayload("kalacak kayıt", "", ""), RecordLineage.Manual(), Ct);
        var remove = await ws.Commands.AddAsync(datasetId, new InstructionPayload("silinecek kayıt", "", ""), RecordLineage.Manual(), Ct);

        var deleted = await ws.Commands.DeleteAsync([remove, Guid.NewGuid()], Ct);

        Assert.Equal(1, deleted);
        Assert.Equal(0, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "silinecek"), Ct));
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "kayıt"), Ct));
        Assert.NotNull(await ws.Queries.GetAsync(keep, Ct));
        Assert.Equal(1, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordSearchKeys"));
    }

    [Fact]
    public async Task Restoring_states_undoes_status_changes_and_edits()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var original = new InstructionPayload("eski soru", "", "eski cevap");
        var id = await ws.Commands.AddAsync(datasetId, original, RecordLineage.Manual(), Ct);
        await ws.Commands.SetStatusAsync([id], RecordStatus.Approved, Ct);
        await ws.Commands.UpdatePayloadAsync(id, new InstructionPayload("yeni soru", "", "yeni cevap"), Ct);

        // Undo the edit: old content, and the status the record had before it (Approved).
        var found = await ws.Commands.RestoreStatesAsync([new RecordState(id, RecordStatus.Approved, original), new RecordState(Guid.NewGuid(), RecordStatus.Draft)], Ct);

        Assert.Equal(1, found);
        var details = await ws.Queries.GetAsync(id, Ct);
        Assert.Equal((RecordStatus.Approved, original), (details!.Status, details.Payload));
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "eski"), Ct));
        Assert.Equal(0, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "yeni"), Ct));

        // Undo the approval: Approved → Draft, which the lifecycle alone would not allow.
        await ws.Commands.RestoreStatesAsync([new RecordState(id, RecordStatus.Draft)], Ct);
        Assert.Equal(RecordStatus.Draft, (await ws.Queries.GetAsync(id, Ct))!.Status);
    }

    [Fact]
    public async Task An_edit_written_with_a_model_notes_it_in_the_lineage_and_undo_takes_it_back()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var lineage = new RecordLineage(RecordSourceKind.FileImport, "sss.csv#row=2");
        var original = new InstructionPayload("Kargo ücreti?", "", "");
        var id = await ws.Commands.AddAsync(datasetId, original, lineage, Ct);

        // Unchanged content changes nothing, not even the lineage.
        Assert.False(await ws.Commands.UpdatePayloadAsync(id, original, "qwen2.5:7b", Ct));
        Assert.Null((await ws.Queries.GetAsync(id, Ct))!.Lineage.GeneratedByModel);

        Assert.True(await ws.Commands.UpdatePayloadAsync(id, new InstructionPayload("Kargo ücreti?", "", "500 TL üzeri ücretsiz."), "qwen2.5:7b", Ct));
        Assert.Equal(lineage with { GeneratedByModel = "qwen2.5:7b" }, (await ws.Queries.GetAsync(id, Ct))!.Lineage);

        await ws.Commands.RestoreStatesAsync([new RecordState(id, RecordStatus.Draft, original, Lineage: lineage)], Ct);
        var details = await ws.Queries.GetAsync(id, Ct);
        Assert.Equal((original, lineage), (details!.Payload, details.Lineage));
    }

    [Fact]
    public async Task Tag_names_are_unique_regardless_of_turkish_case()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        await using (var context = ws.Workspace.CreateDbContext())
        {
            context.Tags.Add(Tag.Create("Şifre", TestWorkspace.Now));
            await context.SaveChangesAsync(Ct);
        }

        await using var other = ws.Workspace.CreateDbContext();
        other.Tags.Add(Tag.Create("ŞİFRE", TestWorkspace.Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Deleted_records_come_back_from_snapshots()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Chat, Ct);
        var payload = new ChatPayload("Kısa yanıt ver.", [new ChatMessage(ChatRole.User, "Şifremi unuttum"), new ChatMessage(ChatRole.Assistant, "Sıfırlayın.")]);
        var lineage = new RecordLineage(RecordSourceKind.FileImport, "chat.jsonl#line=2", "ayse");
        var first = await ws.Commands.AddAsync(datasetId, payload, lineage, Ct);
        var second = await ws.Commands.AddAsync(datasetId, new ChatPayload(null, [new ChatMessage(ChatRole.User, "ikinci")]), RecordLineage.Manual(), Ct);
        await ws.Commands.SetStatusAsync([first], RecordStatus.Approved, Ct);
        var tag = Tag.Create("destek", TestWorkspace.Now);
        await using (var context = ws.Workspace.CreateDbContext())
        {
            context.Tags.Add(tag);
            var record = await context.Records.Include(r => r.Tags).SingleAsync(r => r.Id == first, Ct);
            record.AddTag(tag, TestWorkspace.Now);
            await context.SaveChangesAsync(Ct);
        }

        var before = await ws.Queries.GetAsync(first, Ct);
        var snapshots = await ws.Queries.GetSnapshotsAsync([second, first, Guid.NewGuid()], Ct);
        await ws.Commands.DeleteAsync([first, second], Ct);
        Assert.Equal(0, await ws.Queries.CountAsync(datasetId, RecordFilter.None, Ct));

        var restored = await ws.Commands.RestoreAsync(snapshots, Ct);

        Assert.Equal([first, second], snapshots.Select(s => s.Id));
        Assert.Equal(2, restored);
        Assert.Equal(before, await ws.Queries.GetAsync(first, Ct), new RecordDetailsComparer());
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "şifre"), Ct));
        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct);
        Assert.Equal([1L, 2L], page.Items.Select(r => r.Ordinal));

        // Restoring again is harmless.
        Assert.Equal(0, await ws.Commands.RestoreAsync(snapshots, Ct));
    }

    [Fact]
    public async Task Records_written_from_a_document_keep_their_passage_and_can_be_taken_back()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Instruction, Ct);
        await ws.Commands.AddAsync(datasetId, new InstructionPayload("Önceki", "", ""), RecordLineage.Manual(), Ct);
        const string metadata = """{"source":{"file":"kılavuz.pdf","section":"Kargo","unit":"page","first":3,"last":3,"passage":"Kargo 500 TL üzeri ücretsizdir."}}""";
        var lineage = new RecordLineage(RecordSourceKind.DocumentChunk, "kılavuz.pdf#page=3", GeneratedByModel: "qwen2.5:7b");

        var ids = await ws.Commands.AddManyAsync(datasetId,
        [
            new NewRecord(new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz."), lineage, metadata),
            new NewRecord(new InstructionPayload("Kargo ücretsiz mi?", "", "500 TL üzerinde evet."), lineage, metadata),
        ], Ct);

        Assert.Equal(2, ids.Count);
        var first = await ws.Queries.GetAsync(ids[0], Ct);
        Assert.Equal((2L, RecordStatus.Draft, metadata), (first!.Ordinal, first.Status, first.MetadataJson));
        Assert.Equal(lineage, first.Lineage);
        var earlier = (await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 1, Ct)).Items[0];
        Assert.Equal("{}", (await ws.Queries.GetAsync(earlier.Id, Ct))!.MetadataJson);

        // Undo deletes them; redo brings them back with their passage.
        var snapshots = await ws.Queries.GetSnapshotsAsync(ids, Ct);
        Assert.Equal(2, await ws.Commands.DeleteAsync(ids, Ct));
        Assert.Equal(2, await ws.Commands.RestoreAsync(snapshots, Ct));
        Assert.Equal(first, await ws.Queries.GetAsync(ids[0], Ct), new RecordDetailsComparer());
        Assert.Equal(["Önceki", "Kargo ücreti ne kadar?", "Kargo ücretsiz mi?"],
            (await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 10, Ct)).Items.Select(r => r.Preview));
    }

    [Fact]
    public async Task Restoring_into_a_deleted_dataset_fails()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var id = await ws.Commands.AddAsync(datasetId, new InstructionPayload("a", "", "b"), RecordLineage.Manual(), Ct);
        var snapshots = await ws.Queries.GetSnapshotsAsync([id], Ct);
        await ws.Catalog.DeleteDatasetAsync(datasetId, Ct);

        await Assert.ThrowsAsync<NotFoundException>(() => ws.Commands.RestoreAsync(snapshots, Ct));
    }

    // RecordDetails holds a tag list, which records compare by reference.
    private sealed class RecordDetailsComparer : IEqualityComparer<RecordDetails?>
    {
        public bool Equals(RecordDetails? x, RecordDetails? y) =>
            x is not null && y is not null && x with { TagIds = [] } == y with { TagIds = [] } && x.TagIds.SequenceEqual(y.TagIds);

        public int GetHashCode(RecordDetails? obj) => obj?.Id.GetHashCode() ?? 0;
    }

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
