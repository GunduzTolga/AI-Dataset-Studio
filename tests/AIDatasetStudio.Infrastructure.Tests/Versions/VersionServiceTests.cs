using System.Diagnostics;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Versions;

public sealed class VersionServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_version_freezes_every_record_with_its_status_and_token_count()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Bir"), Q("İki"), Q("Üç"));
        await ws.Commands.SetStatusAsync([ids[0], ids[1]], RecordStatus.Approved, Ct);
        await ws.Commands.SetStatusAsync([ids[2]], RecordStatus.Archived, Ct);
        await ws.Catalog.SetTokenSettingsAsync(datasetId, "cl100k_base", 2_048, Ct);

        var version = await ws.Versions.CreateAsync(datasetId, "v1.0.0", "  İlk eğitim  ", cancellationToken: Ct);

        Assert.Equal(("1.0.0", "İlk eğitim", 3L, 2L), (version.Number, version.Description, version.RecordCount, version.ApprovedCount));
        Assert.Equal(("cl100k_base", 2_048, TestWorkspace.Now), (version.Tokenizer, version.ContextLength, version.CreatedAt));
        Assert.Equal(3, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordRevisions"));
        // The records were analysed first: every mapping has its token count.
        Assert.Equal(3, await ws.ScalarAsync("SELECT COUNT(*) FROM VersionRecordMappings WHERE Tokens > 0"));
        Assert.Equal([version], await ws.Versions.ListAsync(datasetId, Ct));
    }

    [Fact]
    public async Task Unchanged_records_share_their_revision_between_versions()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Bir"), Q("İki"), Q("Üç"));
        await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);
        await ws.Commands.UpdatePayloadAsync(ids[1], Q("İki (düzeltildi)"), Ct);
        ws.Clock.Advance(TimeSpan.FromMinutes(1));

        await ws.Versions.CreateAsync(datasetId, "1.1.0", null, cancellationToken: Ct);

        Assert.Equal(4, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordRevisions")); // one new content
        Assert.Equal(6, await ws.ScalarAsync("SELECT COUNT(*) FROM VersionRecordMappings"));
        Assert.Equal(["1.1.0", "1.0.0"], (await ws.Versions.ListAsync(datasetId, Ct)).Select(v => v.Number));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("sürüm 1")]
    [InlineData("v1.0.0")] // the same number as the existing one
    public async Task Invalid_or_taken_numbers_are_rejected(string number)
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, Q("Bir"));
        await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        await Assert.ThrowsAsync<DomainException>(() => ws.Versions.CreateAsync(datasetId, number, null, cancellationToken: Ct));
        Assert.Single(await ws.Versions.ListAsync(datasetId, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Versions.CreateAsync(Guid.NewGuid(), "2.0.0", null, cancellationToken: Ct));
    }

    [Fact]
    public async Task Comparison_lists_added_removed_modified_and_status_changes_in_order()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Bir"), Q("İki"), Q("Üç"), Q("Dört"));
        var v1 = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        await ws.Commands.UpdatePayloadAsync(ids[0], Q("Bir (yeni)"), Ct);       // modified (and sent to review)
        await ws.Commands.SetStatusAsync([ids[1]], RecordStatus.Approved, Ct);   // status only
        await ws.Commands.DeleteAsync([ids[2]], Ct);                             // removed
        var added = await ws.Commands.AddAsync(datasetId, Q("Beş"), RecordLineage.Manual(), Ct);

        var comparison = await ws.Versions.CompareAsync(datasetId, v1.Id, null, Ct);

        Assert.Equal(
            [
                new RecordDifference(ids[0], DifferenceKind.Modified, 1, "Bir (yeni)", RecordStatus.Draft, RecordStatus.InReview),
                new RecordDifference(ids[1], DifferenceKind.StatusChanged, 2, "İki", RecordStatus.Draft, RecordStatus.Approved),
                new RecordDifference(ids[2], DifferenceKind.Removed, 3, "Üç", RecordStatus.Draft, null),
                new RecordDifference(added, DifferenceKind.Added, 5, "Beş", null, RecordStatus.Draft),
            ],
            comparison.Differences);
        Assert.Equal(1, comparison.Unchanged);

        // The other way round, and between two versions.
        var reversed = await ws.Versions.CompareAsync(datasetId, null, v1.Id, Ct);
        Assert.Equal([DifferenceKind.Modified, DifferenceKind.StatusChanged, DifferenceKind.Added, DifferenceKind.Removed], reversed.Differences.Select(d => d.Kind));
        var v2 = await ws.Versions.CreateAsync(datasetId, "1.1.0", null, cancellationToken: Ct);
        Assert.Equal(comparison.Differences, (await ws.Versions.CompareAsync(datasetId, v1.Id, v2.Id, Ct)).Differences);
        Assert.Empty((await ws.Versions.CompareAsync(datasetId, v2.Id, null, Ct)).Differences);
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Versions.CompareAsync(Guid.NewGuid(), v1.Id, null, Ct));
    }

    [Fact]
    public async Task A_version_keeps_contents_that_were_edited_or_deleted_since()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Eski metin"), Q("Silinecek"));
        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        await ws.Commands.UpdatePayloadAsync(ids[0], Q("Yeni metin"), Ct);
        await ws.Commands.DeleteAsync([ids[1]], Ct);

        Assert.Equal(Q("Eski metin"), await ws.Versions.GetContentAsync(datasetId, version.Id, ids[0], Ct));
        Assert.Equal(Q("Yeni metin"), await ws.Versions.GetContentAsync(datasetId, null, ids[0], Ct));
        Assert.Equal(Q("Silinecek"), await ws.Versions.GetContentAsync(datasetId, version.Id, ids[1], Ct));
        Assert.Null(await ws.Versions.GetContentAsync(datasetId, null, ids[1], Ct));
    }

    [Fact]
    public async Task Going_back_restores_contents_statuses_and_deleted_records_and_archives_new_ones()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Bir"), Q("İki"), Q("Üç"), Q("Dört"));
        await ws.Commands.SetStatusAsync([ids[0], ids[1]], RecordStatus.Approved, Ct);
        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);
        var lineage = (await ws.Queries.GetAsync(ids[2], Ct))!.Lineage;

        await ws.Commands.UpdatePayloadAsync(ids[0], Q("Bir (bozuldu)"), Ct);
        await ws.Commands.SetStatusAsync([ids[1]], RecordStatus.Archived, Ct);
        await ws.Commands.DeleteAsync([ids[2]], Ct);
        var added = await ws.Commands.AddAsync(datasetId, Q("Beş"), RecordLineage.Manual(), Ct);
        ws.Clock.Advance(TimeSpan.FromHours(1));

        var result = await ws.Versions.RollbackAsync(version.Id, Ct);

        Assert.Equal(("1.0.0", 1, 1, 1), (result.Number, result.ContentRestored, result.StatusRestored, result.Archived));
        Assert.Equal(ids[2], Assert.Single(result.Recreated).Id);
        Assert.Equal(4, result.Changed);

        // The working copy is the version again; the record added since stays, archived.
        var after = await ws.Versions.CompareAsync(datasetId, version.Id, null, Ct);
        Assert.Equal([new RecordDifference(added, DifferenceKind.Added, 5, "Beş", null, RecordStatus.Archived)], after.Differences);
        var first = (await ws.Queries.GetAsync(ids[0], Ct))!;
        Assert.Equal((Q("Bir"), RecordStatus.Approved), (first.Payload, first.Status));
        var restored = (await ws.Queries.GetAsync(ids[2], Ct))!;
        Assert.Equal((3L, lineage, TestWorkspace.Now), (restored.Ordinal, restored.Lineage, restored.CreatedAt));
        // The search index follows the restored contents.
        Assert.Equal(1, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "Üç"), Ct));
        Assert.Equal(0, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: "bozuldu"), Ct));

        // Undo puts back what was there before going back.
        await ws.Commands.RestoreStatesAsync(result.Before, Ct);
        await ws.Commands.DeleteAsync([.. result.Recreated.Select(r => r.Id)], Ct);
        Assert.Equal(Q("Bir (bozuldu)"), (await ws.Queries.GetAsync(ids[0], Ct))!.Payload);
        Assert.Equal(RecordStatus.Archived, (await ws.Queries.GetAsync(ids[1], Ct))!.Status);
        Assert.Null(await ws.Queries.GetAsync(ids[2], Ct));
        Assert.Equal(RecordStatus.Draft, (await ws.Queries.GetAsync(added, Ct))!.Status);

        // And redo does it again.
        await ws.Commands.RestoreAsync(result.Recreated, Ct);
        await ws.Commands.RestoreStatesAsync(result.After, Ct);
        Assert.Equal([DifferenceKind.Added], (await ws.Versions.CompareAsync(datasetId, version.Id, null, Ct)).Differences.Select(d => d.Kind));
    }

    [Fact]
    public async Task Going_back_restores_tags_and_brings_deleted_records_back_with_theirs()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Bir"), Q("İki"), Q("Üç"));
        var hukuk = await ws.Tags.CreateAsync("kategori:hukuk", Ct);
        var teknik = await ws.Tags.CreateAsync("kategori:teknik", Ct);
        await ws.Commands.SetTagAsync([ids[0], ids[1]], hukuk.Id, present: true, Ct);
        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        await ws.Commands.DeleteAsync([ids[1]], Ct);
        await ws.Commands.SetTagAsync([ids[0]], hukuk.Id, present: false, Ct);
        await ws.Commands.SetTagAsync([ids[0], ids[2]], teknik.Id, present: true, Ct);

        var comparison = await ws.Versions.CompareAsync(datasetId, version.Id, null, Ct);
        Assert.Equal([DifferenceKind.TagsChanged, DifferenceKind.Removed, DifferenceKind.TagsChanged], comparison.Differences.Select(d => d.Kind));
        Assert.Equal([hukuk.Id], comparison.Differences[0].OldTagIds!);
        Assert.Equal([teknik.Id], comparison.Differences[0].NewTagIds!);

        var result = await ws.Versions.RollbackAsync(version.Id, Ct);

        Assert.Equal((0, 2, 0), (result.ContentRestored, result.StatusRestored, result.Archived));
        Assert.Equal([hukuk.Id], (await ws.Queries.GetAsync(ids[0], Ct))!.TagIds);
        Assert.Equal([hukuk.Id], (await ws.Queries.GetAsync(ids[1], Ct))!.TagIds);
        Assert.Empty((await ws.Queries.GetAsync(ids[2], Ct))!.TagIds);
        Assert.Empty((await ws.Versions.CompareAsync(datasetId, version.Id, null, Ct)).Differences);

        // Undo puts the tags back as they were.
        await ws.Commands.RestoreStatesAsync(result.Before, Ct);
        Assert.Equal([teknik.Id], (await ws.Queries.GetAsync(ids[0], Ct))!.TagIds);
        Assert.Equal([teknik.Id], (await ws.Queries.GetAsync(ids[2], Ct))!.TagIds);
    }

    [Fact]
    public async Task A_deleted_tag_no_longer_counts_as_a_difference()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Bir"));
        var tag = await ws.Tags.CreateAsync("geçici", Ct);
        await ws.Commands.SetTagAsync(ids, tag.Id, present: true, Ct);
        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        await ws.Tags.DeleteAsync(tag.Id, Ct);

        Assert.Empty((await ws.Versions.CompareAsync(datasetId, version.Id, null, Ct)).Differences);
        Assert.Equal(0, (await ws.Versions.RollbackAsync(version.Id, Ct)).Changed);
    }

    [Fact]
    public async Task Going_back_to_the_current_state_changes_nothing()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, Q("Bir"), Q("İki"));
        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        var result = await ws.Versions.RollbackAsync(version.Id, Ct);

        Assert.Equal(0, result.Changed);
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Versions.RollbackAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Deleting_a_version_removes_the_contents_only_it_kept()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, Q("Bir"), Q("İki"));
        var v1 = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);
        await ws.Commands.UpdatePayloadAsync(ids[0], Q("Bir (yeni)"), Ct);
        await ws.Commands.DeleteAsync([ids[1]], Ct);
        var v2 = await ws.Versions.CreateAsync(datasetId, "1.1.0", null, cancellationToken: Ct);
        Assert.Equal(3, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordRevisions"));

        await ws.Versions.DeleteAsync(v1.Id, Ct);

        // "Bir" and the deleted "İki" were only in 1.0.0; "Bir (yeni)" stays for 1.1.0.
        Assert.Equal(1, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordRevisions"));
        Assert.Equal(Q("Bir (yeni)"), await ws.Versions.GetContentAsync(datasetId, v2.Id, ids[0], Ct));
        Assert.Equal(["1.1.0"], (await ws.Versions.ListAsync(datasetId, Ct)).Select(v => v.Number));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Versions.DeleteAsync(v1.Id, Ct));
    }

    [Fact]
    public async Task Deleting_the_dataset_removes_its_versions_and_contents()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, Q("Bir"), Q("İki"));
        await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        await ws.Catalog.DeleteDatasetAsync(datasetId, Ct);

        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM DatasetVersions"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM VersionRecordMappings"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordRevisions"));
    }

    [Fact]
    public async Task Exporting_a_version_writes_it_as_frozen()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await ws.Catalog.SetTokenSettingsAsync(datasetId, "o200k_base", 256, Ct);
        var ids = await AddAsync(ws, datasetId,
            Q("Başkent neresi?", "Ankara."),
            Q("Uzun", string.Join(' ', Enumerable.Repeat("kelime", 400))),  // over 256 tokens
            Q("Başkent neresi?", "Ankara."),                                // copy
            Q("Taslak", "Henüz değil."));
        await ws.Commands.SetStatusAsync([ids[0], ids[1], ids[2]], RecordStatus.Approved, Ct);
        var options = ExportOptions.DefaultFor(ExportFormat.Alpaca) with { Split = SplitOptions.Standard };
        var atVersion = Path.Combine(ws.Directory, "at-version");
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        await ws.Export.ExportAsync(datasetId, options, atVersion, cancellationToken: Ct);
        var version = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);

        // The dataset moves on: an edit, an approval, a deletion, a larger context length.
        await ws.Commands.UpdatePayloadAsync(ids[0], Q("Başkent neresi?", "Ankara'dır."), Ct);
        await ws.Commands.SetStatusAsync([ids[3]], RecordStatus.Approved, Ct);
        await ws.Commands.DeleteAsync([ids[2]], Ct);
        await ws.Catalog.SetTokenSettingsAsync(datasetId, "o200k_base", 4_096, Ct);
        ws.Clock.Advance(TimeSpan.FromDays(30));

        var later = Path.Combine(ws.Directory, "later");
        var preview = await ws.Export.PreviewAsync(datasetId, options, 0, version.Id, Ct);
        var result = await ws.Export.ExportAsync(datasetId, options, later, versionId: version.Id, cancellationToken: Ct);

        // The same files as when the version was made, and counts that match the preview.
        foreach (var file in Directory.EnumerateFiles(atVersion))
        {
            Assert.Equal(await File.ReadAllTextAsync(file, Ct), await File.ReadAllTextAsync(Path.Combine(later, Path.GetFileName(file)), Ct));
        }

        Assert.Equal((3L, 1L, 1L, 1L), (preview.Selected, preview.Exported, preview.OverContextLimit, preview.Duplicates));
        Assert.Equal((1L, 1L, 1L), (result.Exported, result.OverContextLimit, result.Duplicates));
        Assert.Equal("1.0.0", (await ws.Export.GetLastExportAsync(datasetId, Ct))!.VersionNumber);

        // The history keeps the number after the version is deleted.
        await ws.Versions.DeleteAsync(version.Id, Ct);
        Assert.Equal("1.0.0", (await ws.Export.GetLastExportAsync(datasetId, Ct))!.VersionNumber);
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Export.PreviewAsync(datasetId, options, 0, version.Id, Ct));
    }

    [Fact]
    public async Task Versions_of_ten_thousand_records_are_quick()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await ws.Commands.AddRangeAsync(
            datasetId,
            Enumerable.Range(0, 10_000).Select(i => new NewRecord(Q($"Soru {i}: siparişim nerede?", $"Yanıt {i}: kargo takip sayfasına bakın."), RecordLineage.Manual())),
            cancellationToken: Ct);
        await ws.Analysis.AnalyzeAsync(datasetId, cancellationToken: Ct);
        var stopwatch = Stopwatch.StartNew();

        var v1 = await ws.Versions.CreateAsync(datasetId, "1.0.0", null, cancellationToken: Ct);
        var created = stopwatch.Elapsed;
        var comparison = await ws.Versions.CompareAsync(datasetId, v1.Id, null, Ct);
        var compared = stopwatch.Elapsed - created;

        TestContext.Current.TestOutputHelper?.WriteLine($"create {created.TotalMilliseconds:F0} ms, compare {compared.TotalMilliseconds:F0} ms");
        Assert.Equal((10_000L, 10_000L), (v1.RecordCount, comparison.Unchanged));
        Assert.Empty(comparison.Differences);
    }

    private static InstructionPayload Q(string question, string answer = "Yanıt") => new(question, "", answer);

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
