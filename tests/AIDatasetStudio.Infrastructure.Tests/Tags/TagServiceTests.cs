using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Tags;

public sealed class TagServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_tags_get_the_next_color_and_a_free_shortcut()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);

        var created = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            created.Add((await ws.Tags.CreateAsync($"etiket {i}", Ct)).Name);
        }

        var tags = await ws.Tags.ListAsync(cancellationToken: Ct);
        Assert.Equal(created, tags.Select(t => t.Name));
        Assert.Equal([1, 2, 3, 4, 5, null], tags.Select(t => t.Shortcut));
        Assert.Equal(Tag.Palette.Take(6), tags.Select(t => t.ColorHex));
    }

    [Theory]
    [InlineData("HUKUK")]
    [InlineData("hukuk")]
    [InlineData("  Hukuk ")]
    public async Task Names_are_unique_ignoring_case(string name)
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        await ws.Tags.CreateAsync("Hukuk", Ct);

        await Assert.ThrowsAsync<DomainException>(() => ws.Tags.CreateAsync(name, Ct));
    }

    [Fact]
    public async Task Turkish_dotted_and_dotless_i_count_as_the_same_letter()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var first = await ws.Tags.CreateAsync("İade", Ct);
        var other = await ws.Tags.CreateAsync("bilgi", Ct);

        await Assert.ThrowsAsync<DomainException>(() => ws.Tags.CreateAsync("iade", Ct));
        await Assert.ThrowsAsync<DomainException>(() => ws.Tags.UpdateAsync(other.Id, "IADE", other.ColorHex, other.Shortcut, Ct));
        await ws.Tags.UpdateAsync(first.Id, "iade", first.ColorHex, first.Shortcut, Ct); // its own name, recased
        Assert.Equal("iade", (await ws.Tags.ListAsync(cancellationToken: Ct))[1].Name);
    }

    [Fact]
    public async Task Update_renames_recolors_and_moves_a_taken_shortcut()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var hukuk = await ws.Tags.CreateAsync("hukuk", Ct);
        var teknik = await ws.Tags.CreateAsync("teknik", Ct);

        await ws.Tags.UpdateAsync(teknik.Id, "kategori:teknik", "#10b981", hukuk.Shortcut, Ct);

        var tags = (await ws.Tags.ListAsync(cancellationToken: Ct)).ToDictionary(t => t.Id);
        Assert.Equal(("kategori:teknik", "#10B981", 1), (tags[teknik.Id].Name, tags[teknik.Id].ColorHex, tags[teknik.Id].Shortcut));
        Assert.Null(tags[hukuk.Id].Shortcut);
        Assert.Equal("kategori", tags[teknik.Id].Group);
        await Assert.ThrowsAsync<DomainException>(() => ws.Tags.UpdateAsync(teknik.Id, "x", "red", null, Ct));
        await Assert.ThrowsAsync<DomainException>(() => ws.Tags.UpdateAsync(teknik.Id, "x", "#000000", 9, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Tags.UpdateAsync(Guid.NewGuid(), "x", "#000000", null, Ct));
    }

    [Fact]
    public async Task Records_are_tagged_untagged_and_counted_per_dataset()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var first = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var second = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, first, 4);
        var otherIds = await AddAsync(ws, second, 1);
        var tag = await ws.Tags.CreateAsync("hukuk", Ct);

        Assert.Equal(ids.Take(2), await ws.Commands.SetTagAsync([ids[1], ids[0]], tag.Id, present: true, Ct));
        Assert.Equal([ids[2]], await ws.Commands.SetTagAsync([ids[0], ids[2]], tag.Id, present: true, Ct)); // #1 already had it
        await ws.Commands.SetTagAsync(otherIds, tag.Id, present: true, Ct);
        Assert.Equal([ids[1]], await ws.Commands.SetTagAsync([ids[1], ids[3]], tag.Id, present: false, Ct));

        Assert.Equal(2, (await ws.Tags.ListAsync(first, Ct)).Single().RecordCount);
        Assert.Equal(3, (await ws.Tags.ListAsync(cancellationToken: Ct)).Single().RecordCount);
        Assert.Equal([tag.Id], (await ws.Queries.GetAsync(ids[0], Ct))!.TagIds);
        // Tags do not touch the content or the status.
        Assert.Equal(RecordStatus.Draft, (await ws.Queries.GetAsync(ids[0], Ct))!.Status);
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Commands.SetTagAsync(ids, Guid.NewGuid(), present: true, Ct));
    }

    [Fact]
    public async Task Lists_filter_and_count_by_tag()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, 5);
        var hukuk = await ws.Tags.CreateAsync("hukuk", Ct);
        var teknik = await ws.Tags.CreateAsync("teknik", Ct);
        await ws.Commands.SetTagAsync([ids[0], ids[1], ids[2]], hukuk.Id, present: true, Ct);
        await ws.Commands.SetTagAsync([ids[2]], teknik.Id, present: true, Ct);
        await ws.Commands.SetStatusAsync([ids[0]], RecordStatus.Approved, Ct);

        var tagged = await ws.Queries.GetPageAsync(datasetId, new RecordFilter(TagId: hukuk.Id), null, 10, Ct);
        var untagged = await ws.Queries.GetPageAsync(datasetId, new RecordFilter(Untagged: true), null, 10, Ct);
        var counts = await ws.Queries.CountByStatusAsync(datasetId, new RecordFilter(TagId: hukuk.Id), Ct);

        Assert.Equal(ids.Take(3), tagged.Items.Select(r => r.Id));
        Assert.Equal(ids.Skip(3), untagged.Items.Select(r => r.Id));
        Assert.Equal((1L, 2L), (counts[RecordStatus.Approved], counts[RecordStatus.Draft]));
        Assert.Equal(new HashSet<Guid> { hukuk.Id, teknik.Id }, tagged.Items[2].Tags.ToHashSet());
        Assert.Empty(untagged.Items[0].Tags);
        Assert.Equal(2, await ws.Queries.CountAsync(datasetId, new RecordFilter(Untagged: true), Ct));
    }

    [Fact]
    public async Task Deleting_a_tag_removes_it_from_its_records()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, 3);
        var tag = await ws.Tags.CreateAsync("geçici", Ct);
        await ws.Commands.SetTagAsync(ids, tag.Id, present: true, Ct);

        Assert.Equal(3, await ws.Tags.DeleteAsync(tag.Id, Ct));

        Assert.Empty(await ws.Tags.ListAsync(cancellationToken: Ct));
        Assert.Empty((await ws.Queries.GetAsync(ids[0], Ct))!.TagIds);
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordTags"));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Tags.DeleteAsync(tag.Id, Ct));
    }

    [Fact]
    public async Task Undo_and_redo_restore_tags_through_record_states()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, 1);
        var tag = await ws.Tags.CreateAsync("hukuk", Ct);
        await ws.Commands.SetTagAsync(ids, tag.Id, present: true, Ct);

        await ws.Commands.RestoreStatesAsync([new RecordState(ids[0], RecordStatus.Draft, TagIds: [])], Ct);
        Assert.Empty((await ws.Queries.GetAsync(ids[0], Ct))!.TagIds);

        // A tag that no longer exists is left out instead of failing.
        await ws.Commands.RestoreStatesAsync([new RecordState(ids[0], RecordStatus.Draft, TagIds: [tag.Id, Guid.NewGuid()])], Ct);
        Assert.Equal([tag.Id], (await ws.Queries.GetAsync(ids[0], Ct))!.TagIds);
    }

    private static async Task<List<Guid>> AddAsync(TestWorkspace ws, Guid datasetId, int count)
    {
        var ids = new List<Guid>();
        for (var i = 1; i <= count; i++)
        {
            ids.Add(await ws.Commands.AddAsync(datasetId, new InstructionPayload($"Soru {i}", "", "Yanıt"), RecordLineage.Manual(), Ct));
        }

        return ids;
    }
}
