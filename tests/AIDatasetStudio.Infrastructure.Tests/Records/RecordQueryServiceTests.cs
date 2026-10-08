using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Records;

public sealed class RecordQueryServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pages_cover_every_record_once_in_order()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, Enumerable.Range(1, 25).Select(i => $"Kayıt {i}"));

        var seen = new List<long>();
        RecordCursor? cursor = null;
        var pages = 0;
        do
        {
            var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, cursor, pageSize: 10, Ct);
            seen.AddRange(page.Items.Select(r => r.Ordinal));
            cursor = page.Next;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), seen);
    }

    [Fact]
    public async Task Last_full_page_has_no_next_cursor()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, ["a", "b"]);

        var page = await ws.Queries.GetPageAsync(datasetId, RecordFilter.None, after: null, pageSize: 2, Ct);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task Filters_by_status()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, ["a", "b", "c", "d"]);
        await ws.Commands.SetStatusAsync([ids[1], ids[3]], RecordStatus.Approved, Ct);
        await ws.Commands.SetStatusAsync([ids[2]], RecordStatus.Archived, Ct);

        var approved = new RecordFilter(Statuses: new HashSet<RecordStatus> { RecordStatus.Approved });
        var draftOrArchived = new RecordFilter(Statuses: new HashSet<RecordStatus> { RecordStatus.Draft, RecordStatus.Archived });

        Assert.Equal([ids[1], ids[3]], (await ws.Queries.GetPageAsync(datasetId, approved, null, 10, Ct)).Items.Select(r => r.Id));
        Assert.Equal([ids[0], ids[2]], (await ws.Queries.GetPageAsync(datasetId, draftOrArchived, null, 10, Ct)).Items.Select(r => r.Id));
        Assert.Equal(2, await ws.Queries.CountAsync(datasetId, approved, Ct));
    }

    [Fact]
    public async Task Counts_records_per_status_with_optional_search()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, datasetId, ["izin a", "izin b", "şifre c", "izin d"]);
        await ws.Commands.SetStatusAsync([ids[0], ids[2]], RecordStatus.Approved, Ct);

        var all = await ws.Queries.CountByStatusAsync(datasetId, searchText: null, cancellationToken: Ct);
        var izin = await ws.Queries.CountByStatusAsync(datasetId, "izin", cancellationToken: Ct);

        Assert.Equal(2, all[RecordStatus.Draft]);
        Assert.Equal(2, all[RecordStatus.Approved]);
        Assert.Equal(0, all[RecordStatus.Archived]);
        Assert.Equal(4, all.Count);
        Assert.Equal(2, izin[RecordStatus.Draft]);
        Assert.Equal(1, izin[RecordStatus.Approved]);
    }

    [Theory]
    [InlineData("izin", 1)]
    [InlineData("İZİN", 1)]
    [InlineData("ılık", 1)] // indexed text contains "ILIK"
    [InlineData("sifre", 1)] // indexed text contains "şifre"
    [InlineData("kıd", 1)] // prefix of "kıdeminizi"
    [InlineData("yıllık izin", 1)]
    [InlineData("izin şifre", 0)] // all words must match the same record
    [InlineData("parola", 1)]
    [InlineData("tatil", 0)]
    public async Task Search_handles_turkish_text(string query, int expected)
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, [
            "Yıllık İzin hakkım ne zaman başlar? 1 yıllık kıdeminizi doldurduğunuzda.",
            "Şifremi unuttum, şifre nasıl sıfırlanır? Parola ekranından.",
            "SU ILIK OLMALI",
        ]);

        Assert.Equal(expected, await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: query), Ct));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("a\"b")]
    [InlineData("AND OR NOT")]
    [InlineData("izin*")]
    [InlineData("(izin")]
    [InlineData("NEAR(izin şifre)")]
    [InlineData("--")]
    public async Task Search_never_fails_on_fts_syntax(string query)
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, ["izin şifre"]);

        var count = await ws.Queries.CountAsync(datasetId, new RecordFilter(SearchText: query), Ct);

        Assert.InRange(count, 0, 1);
    }

    [Fact]
    public async Task Search_is_limited_to_the_dataset_and_combines_with_status()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var first = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var second = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var ids = await AddAsync(ws, first, ["izin bir", "izin iki"]);
        await AddAsync(ws, second, ["izin üç"]);
        await ws.Commands.SetStatusAsync([ids[1]], RecordStatus.Approved, Ct);

        var search = new RecordFilter(SearchText: "izin");
        var approvedSearch = search with { Statuses = new HashSet<RecordStatus> { RecordStatus.Approved } };

        Assert.Equal(2, await ws.Queries.CountAsync(first, search, Ct));
        Assert.Equal(1, await ws.Queries.CountAsync(second, search, Ct));
        Assert.Equal([ids[1]], (await ws.Queries.GetPageAsync(first, approvedSearch, null, 10, Ct)).Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Search_pages_continue_after_the_cursor()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await AddAsync(ws, datasetId, Enumerable.Range(1, 30).Select(i => i % 2 == 0 ? $"çift {i}" : $"tek {i}"));
        var filter = new RecordFilter(SearchText: "çift");

        var first = await ws.Queries.GetPageAsync(datasetId, filter, null, 10, Ct);
        var second = await ws.Queries.GetPageAsync(datasetId, filter, first.Next, 10, Ct);

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)(i * 2)), first.Items.Select(r => r.Ordinal));
        Assert.Equal(Enumerable.Range(11, 5).Select(i => (long)(i * 2)), second.Items.Select(r => r.Ordinal));
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task Details_include_payload_lineage_and_tags()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(DatasetType.Preference, Ct);
        var payload = new PreferencePayload("p", "chosen", "rejected");
        var id = await ws.Commands.AddAsync(datasetId, payload, new RecordLineage(RecordSourceKind.Synthetic, GeneratedByModel: "llama3"), Ct);

        var details = await ws.Queries.GetAsync(id, Ct);

        Assert.NotNull(details);
        Assert.Equal(payload, details.Payload);
        Assert.Equal("llama3", details.Lineage.GeneratedByModel);
        Assert.Empty(details.Tags);
        Assert.Equal(datasetId, details.DatasetId);
        Assert.Null(await ws.Queries.GetAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Page_size_is_bounded()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ws.Queries.GetPageAsync(datasetId, RecordFilter.None, null, 5_000, Ct));
    }

    private static async Task<List<Guid>> AddAsync(TestWorkspace ws, Guid datasetId, IEnumerable<string> instructions)
    {
        var ids = new List<Guid>();
        foreach (var text in instructions)
        {
            ids.Add(await ws.Commands.AddAsync(datasetId, new InstructionPayload(text, "", ""), RecordLineage.Manual(), Ct));
        }

        return ids;
    }
}
