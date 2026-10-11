using System.Net;
using System.Text;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

/// <summary>
/// Datasets from the Hugging Face Hub (M25), against a fake Hub.
/// </summary>
public sealed class HuggingFaceHubTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("adstudio-hub-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("tatsu-lab/alpaca", "tatsu-lab/alpaca")]
    [InlineData("  https://huggingface.co/datasets/tatsu-lab/alpaca/tree/main/data ", "tatsu-lab/alpaca")]
    [InlineData("huggingface.co/datasets/merve/turkish_instructions", "merve/turkish_instructions")]
    [InlineData("hf://datasets/a.b/c-d", "a.b/c-d")]
    [InlineData("https://huggingface.co/datasets/HuggingFaceH4/ultrachat_200k/viewer/default/train_sft?row=3#x", "HuggingFaceH4/ultrachat_200k")]
    [InlineData("https://www.huggingface.co/datasets/a/b", "a/b")]
    [InlineData("alpaca", null)]
    [InlineData("https://example.com/x/y", null)]
    [InlineData("../etc", null)]
    [InlineData("a/b c", null)]
    public void Dataset_ids_are_read_from_ids_and_page_addresses(string text, string? id) =>
        Assert.Equal(id, IHuggingFaceHub.ParseDatasetId(text));

    [Fact]
    public async Task Importable_files_are_listed_page_by_page_and_downloaded_once()
    {
        var hub = new FakeHub();
        hub.Pages["/api/datasets/tatsu-lab/alpaca/tree/main?recursive=true"] = (
            """[{"type":"directory","path":"data"},{"type":"file","path":"README.md","size":10},{"type":"file","path":"data/train-00000-of-00001.parquet","size":12,"lfs":{"size":5}}]""",
            "<https://huggingface.co/api/datasets/tatsu-lab/alpaca/tree/main?recursive=true&cursor=2>; rel=\"next\"");
        hub.Pages["/api/datasets/tatsu-lab/alpaca/tree/main?recursive=true&cursor=2"] = ("""[{"type":"file","path":"alpaca data.json","size":3},{"type":"file","path":"image.png","size":1}]""", null);
        hub.Files["/datasets/tatsu-lab/alpaca/resolve/main/data/train-00000-of-00001.parquet"] = "PAR1!";
        using var client = new HuggingFaceHub(_folder, hub, token: "hf_secret");

        var files = await client.ListFilesAsync("https://huggingface.co/datasets/tatsu-lab/alpaca", Ct);

        Assert.Equal([new HubFile("alpaca data.json", 3), new HubFile("data/train-00000-of-00001.parquet", 5)], files);
        var received = new List<long>();
        var path = await client.DownloadAsync("tatsu-lab/alpaca", files[1], new InlineProgress<long>(received.Add), Ct);

        Assert.Equal(Path.Combine(_folder, "tatsu-lab_alpaca", "data", "train-00000-of-00001.parquet"), path);
        Assert.Equal("PAR1!", await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(5, received[^1]);
        Assert.All(hub.Requests, r => Assert.Equal("Bearer hf_secret", r.Authorization));

        // Downloaded whole already: not fetched again.
        var requests = hub.Requests.Count;
        Assert.Equal(path, await client.DownloadAsync("tatsu-lab/alpaca", files[1], cancellationToken: Ct));
        Assert.Equal(requests, hub.Requests.Count);
    }

    [Fact]
    public async Task Missing_gated_and_failing_datasets_are_told_apart_and_nothing_is_left_half_written()
    {
        var hub = new FakeHub();
        hub.Statuses["/api/datasets/yok/yok/tree/main?recursive=true"] = HttpStatusCode.NotFound;
        hub.Statuses["/api/datasets/meta-llama/gated/tree/main?recursive=true"] = HttpStatusCode.Unauthorized;
        hub.Statuses["/datasets/a/b/resolve/main/train.jsonl"] = HttpStatusCode.InternalServerError;
        using var client = new HuggingFaceHub(_folder, hub, token: "");

        Assert.Equal(HubErrorKind.NotFound, (await Assert.ThrowsAsync<HubException>(() => client.ListFilesAsync("yok/yok", Ct))).Kind);
        Assert.Equal(HubErrorKind.AccessDenied, (await Assert.ThrowsAsync<HubException>(() => client.ListFilesAsync("meta-llama/gated", Ct))).Kind);
        Assert.Equal(HubErrorKind.InvalidId, (await Assert.ThrowsAsync<HubException>(() => client.ListFilesAsync("alpaca", Ct))).Kind);
        Assert.Equal(HubErrorKind.Unavailable, (await Assert.ThrowsAsync<HubException>(() => client.DownloadAsync("a/b", new HubFile("train.jsonl", null), cancellationToken: Ct))).Kind);
        foreach (var path in new[] { "../x.jsonl", "data/..\\..\\..\\x.csv", "C:\\Users\\x.csv", "data/C:x.csv" })
        {
            // Refused before anything is asked of the server or written.
            Assert.Equal(HubErrorKind.NotFound, Assert.Throws<HubException>(() => client.LocalPath("a/b", path)).Kind);
        }

        // A file list that is not one.
        hub.Pages["/api/datasets/a/b/tree/main?recursive=true"] = ("""{"error": "x"}""", null);
        Assert.Equal(HubErrorKind.Unavailable, (await Assert.ThrowsAsync<HubException>(() => client.ListFilesAsync("a/b", Ct))).Kind);
        hub.Pages["/api/datasets/a/b/tree/main?recursive=true"] = ("[{", null);
        Assert.Equal(HubErrorKind.Unavailable, (await Assert.ThrowsAsync<HubException>(() => client.ListFilesAsync("a/b", Ct))).Kind);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories));
        Assert.All(hub.Requests, r => Assert.Null(r.Authorization));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed record Request(string PathAndQuery, string? Authorization);

    private sealed class FakeHub : HttpMessageHandler
    {
        public Dictionary<string, (string Json, string? Link)> Pages { get; } = [];

        public Dictionary<string, string> Files { get; } = [];

        public Dictionary<string, HttpStatusCode> Statuses { get; } = [];

        public List<Request> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            Assert.Equal("huggingface.co", request.RequestUri.Host);
            Requests.Add(new Request(key, request.Headers.Authorization?.ToString()));
            HttpResponseMessage response;
            if (Statuses.TryGetValue(key, out var status))
            {
                response = new HttpResponseMessage(status);
            }
            else if (Pages.TryGetValue(key, out var page))
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.Json, Encoding.UTF8, "application/json") };
                if (page.Link is { } link)
                {
                    response.Headers.TryAddWithoutValidation("Link", link);
                }
            }
            else if (Files.TryGetValue(key, out var content))
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
