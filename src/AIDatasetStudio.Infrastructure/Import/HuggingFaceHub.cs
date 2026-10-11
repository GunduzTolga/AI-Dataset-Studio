using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// The Hub's public HTTP API (M25): <c>/api/datasets/{id}/tree/main?recursive=true</c> lists files
/// page by page (the <c>Link</c> header names the next page) and <c>/datasets/{id}/resolve/main/{path}</c>
/// serves one. Downloads go to a folder of this computer, named after the dataset.
/// </summary>
/// <param name="downloadDirectory">Where downloaded files are kept.</param>
/// <param name="handler">The HTTP handler; replaced in tests.</param>
/// <param name="token">An access token for gated and private datasets; by default <c>HF_TOKEN</c>.</param>
internal sealed class HuggingFaceHub(string downloadDirectory, HttpMessageHandler? handler = null, string? token = null) : IHuggingFaceHub, IDisposable
{
    public const string BaseUrl = "https://huggingface.co";

    private const long ProgressStep = 1024 * 1024;

    private static readonly string[] Importable = [".parquet", ".jsonl", ".ndjson", ".json", ".csv", ".tsv"];

    private readonly HttpClient _http = CreateClient(handler, token ?? Environment.GetEnvironmentVariable("HF_TOKEN"));

    public async Task<IReadOnlyList<HubFile>> ListFilesAsync(string datasetId, CancellationToken cancellationToken = default)
    {
        var id = Id(datasetId);
        var files = new List<HubFile>();
        var next = new Uri($"{BaseUrl}/api/datasets/{id}/tree/main?recursive=true", UriKind.Absolute);
        // A dataset with very many files is listed far enough to choose from.
        for (var page = 0; next is not null && page < 20; page++)
        {
            using var response = await SendAsync(next, id, cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, id, cancellationToken).ConfigureAwait(false);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type) && type.GetString() == "file"
                    && entry.TryGetProperty("path", out var path) && path.GetString() is { } name
                    && Importable.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(new HubFile(name, Size(entry)));
                }
            }

            next = NextPage(response);
        }

        return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    public async Task<string> DownloadAsync(string datasetId, HubFile file, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var id = Id(datasetId);
        var target = LocalPath(id, file.Path);
        if (file.Size is { } size && File.Exists(target) && new FileInfo(target).Length == size)
        {
            return target;
        }

        var partial = target + ".part";
        var path = string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var response = await SendAsync(new Uri($"{BaseUrl}/datasets/{id}/resolve/main/{path}", UriKind.Absolute), id, cancellationToken).ConfigureAwait(false);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    // Every megabyte is enough for a progress line.
                    if (received - reported >= ProgressStep)
                    {
                        progress?.Report(reported = received);
                    }
                }

                progress?.Report(received);
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            File.Delete(partial);
            throw new HubException(HubErrorKind.Unavailable, $"'{file.Path}' of {id} could not be downloaded: {ex.Message}", ex);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Where a file of a dataset is kept: one folder per dataset, the file's folders kept.
    /// </summary>
    internal string LocalPath(string id, string path)
    {
        // The path comes from the server: it must stay a relative path inside the dataset's folder
        // on every system ("..\\", "C:" and the like are refused).
        var folder = Path.GetFullPath(Path.Combine(downloadDirectory, id.Replace('/', '_')));
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || parts.Any(part => part is "." or ".." || part.IndexOfAny(['\\', ':']) >= 0 || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new HubException(HubErrorKind.NotFound, $"'{path}' is not a file path.");
        }

        var full = Path.GetFullPath(Path.Combine([folder, .. parts]));
        return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new HubException(HubErrorKind.NotFound, $"'{path}' is not a file path.");
    }

    private static string Id(string datasetId) =>
        IHuggingFaceHub.ParseDatasetId(datasetId) ?? throw new HubException(HubErrorKind.InvalidId, $"'{datasetId}' is not a Hugging Face dataset id.");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string id, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                throw new JsonException("The file list is not an array.");
            }

            return document;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            throw new HubException(HubErrorKind.Unavailable, $"The file list of {id} could not be read: {ex.Message}", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string id, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HubException(HubErrorKind.Unavailable, $"Hugging Face could not be reached: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HubException(HubErrorKind.Unavailable, "Hugging Face did not answer in time.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        response.Dispose();
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new HubException(HubErrorKind.NotFound, $"{id} was not found on Hugging Face."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new HubException(HubErrorKind.AccessDenied, $"{id} is gated or private."),
            _ => new HubException(HubErrorKind.Unavailable, $"Hugging Face answered {(int)response.StatusCode} for {id}."),
        };
    }

    private static long? Size(JsonElement entry) =>
        entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object && lfs.TryGetProperty("size", out var lfsSize) && lfsSize.TryGetInt64(out var big)
            ? big
            : entry.TryGetProperty("size", out var size) && size.TryGetInt64(out var small) ? small : null;

    // Link: <https://huggingface.co/api/datasets/…?cursor=…>; rel="next"
    private static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var link in values.SelectMany(v => v.Split(',')))
        {
            var parts = link.Split(';');
            if (parts.Length > 1 && parts.Skip(1).Any(p => p.Trim() is "rel=\"next\"" or "rel=next")
                && Uri.TryCreate(parts[0].Trim().TrimStart('<').TrimEnd('>'), UriKind.Absolute, out var uri)
                && uri.Host == new Uri(BaseUrl).Host)
            {
                return uri;
            }
        }

        return null;
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler, string? token)
    {
        var client = handler is null
            ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All })
            : new HttpClient(handler, disposeHandler: false);
        // Large files take long; silence is caught by the connect timeout and cancellation.
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AIDatasetStudio", typeof(HuggingFaceHub).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        return client;
    }
}
