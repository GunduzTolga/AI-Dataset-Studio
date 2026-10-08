using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Infrastructure.AI;

/// <summary>
/// Talks to a server with the OpenAI chat completions API: Ollama, LM Studio, vLLM, LocalAI and
/// others. Answers are streamed as server-sent events; a server that answers in one piece works too.
/// </summary>
internal sealed class OpenAICompatibleProvider : IAIProvider
{
    /// <summary>
    /// How long the server may stay silent before generation is given up: long enough for a large
    /// model to load on a CPU, short enough that a hung server is noticed.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private const int MaxDetailLength = 300;

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly TimeSpan _idleTimeout;

    public OpenAICompatibleProvider(HttpClient http, AIConnection connection, TimeSpan? idleTimeout = null)
    {
        _http = http;
        Connection = connection;
        _baseUrl = AIProviders.ParseUrl(connection.BaseUrl)
            ?? throw new AIException(AIErrorKind.InvalidUrl, $"'{connection.BaseUrl}' is not an http(s) address.");
        _idleTimeout = idleTimeout ?? IdleTimeout;
    }

    public AIConnection Connection { get; }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "models");
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        try
        {
            var models = JsonNode.Parse(body)?["data"]?.AsArray()
                .Select(model => model?["id"]?.GetValue<string>())
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return models ?? throw NotAnApi(null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw NotAnApi(ex);
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new JsonObject
        {
            ["model"] = Connection.Model,
            ["messages"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = Role(m.Role),
                ["content"] = m.Content,
            })]),
            ["stream"] = true,
            ["temperature"] = request.Temperature,
        };
        if (request.MaxTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        // Silence is measured from the request until each piece of the answer.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);
        using var message = Request(HttpMethod.Post, "chat/completions");
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await SendAsync(message, idle.Token, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
        {
            // The server ignored "stream": the whole answer comes as one JSON document.
            var whole = await ReadBodyAsync(response, idle.Token, cancellationToken);
            yield return Content(whole, streamed: false) ?? string.Empty;
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await ReadLineAsync(reader, idle, cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                yield break;
            }

            if (Content(data, streamed: true) is { Length: > 0 } piece)
            {
                yield return piece;
            }
        }
    }

    private static string Role(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };

    /// <summary>
    /// The text of one streamed piece or of a whole answer; reasoning fields are left out.
    /// </summary>
    private static string? Content(string json, bool streamed)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw NotAnApi(ex);
        }

        if (node?["error"] is { } error)
        {
            throw new AIException(AIErrorKind.ServerError, "The server reported an error while answering.", ErrorText(error));
        }

        var choice = node?["choices"]?.AsArray().FirstOrDefault();
        var content = choice?[streamed ? "delta" : "message"]?["content"];
        return content?.GetValueKind() == JsonValueKind.String ? content.GetValue<string>() : null;
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUrl.AbsoluteUri.TrimEnd('/') + "/" + path));
        if (!string.IsNullOrWhiteSpace(Connection.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Connection.ApiKey.Trim());
        }

        return request;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken, cancellationToken);

    /// <param name="token">Cancels the request, also when the server is silent too long.</param>
    /// <param name="userToken">Cancelled only by the caller; tells a stop from a timeout.</param>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, CancellationToken userToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (Exception ex) when (Translate(ex, userToken) is { } error)
        {
            throw error;
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var detail = await ReadErrorAsync(response, token);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new AIException(AIErrorKind.Unauthorized, "The server refused the API key.", detail),
                HttpStatusCode.NotFound when detail?.Contains("model", StringComparison.OrdinalIgnoreCase) == true =>
                    new AIException(AIErrorKind.ModelNotFound, $"The server has no model '{Connection.Model}'.", detail),
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                    new AIException(AIErrorKind.NotAnApi, $"'{request.RequestUri}' is not an OpenAI-compatible API.", detail),
                _ when (int)response.StatusCode < 500 =>
                    new AIException(AIErrorKind.Rejected, $"The server refused the request ({(int)response.StatusCode}).", detail),
                _ => new AIException(AIErrorKind.ServerError, $"The server failed ({(int)response.StatusCode}).", detail),
            };
        }
    }

    private Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ReadBodyAsync(response, cancellationToken, cancellationToken);

    private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token, CancellationToken userToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(token);
        }
        catch (Exception ex) when (Translate(ex, userToken) is { } error)
        {
            throw error;
        }
    }

    private async Task<string?> ReadLineAsync(StreamReader reader, CancellationTokenSource idle, CancellationToken userToken)
    {
        try
        {
            var line = await reader.ReadLineAsync(idle.Token);
            idle.CancelAfter(_idleTimeout);
            return line;
        }
        catch (Exception ex) when (Translate(ex, userToken) is { } error)
        {
            throw error;
        }
    }

    /// <summary>
    /// A network failure as an <see cref="AIException"/>; <c>null</c> for the caller's own cancellation.
    /// </summary>
    private AIException? Translate(Exception exception, CancellationToken userToken) => exception switch
    {
        OperationCanceledException when userToken.IsCancellationRequested => null,
        OperationCanceledException => new AIException(AIErrorKind.Timeout, $"'{_baseUrl}' stopped answering.", innerException: exception),
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError }
            or HttpRequestException { InnerException: SocketException } =>
            new AIException(AIErrorKind.Unreachable, $"Nothing answers at '{_baseUrl}'.", exception.Message, exception),
        HttpRequestException or IOException => new AIException(AIErrorKind.ServerError, $"The connection to '{_baseUrl}' failed.", exception.Message, exception),
        _ => null,
    };

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(body)?["error"] is { } error)
            {
                return ErrorText(error);
            }
        }
        catch (JsonException)
        {
            // Not JSON: the body itself is the explanation.
        }

        return Shorten(body);
    }

    // OpenAI: {"error": {"message": "…"}}; Ollama's native API: {"error": "…"}.
    private static string? ErrorText(JsonNode error) => Shorten(error.GetValueKind() == JsonValueKind.String
        ? error.GetValue<string>()
        : error["message"]?.GetValueKind() == JsonValueKind.String ? error["message"]!.GetValue<string>() : error.ToJsonString());

    private static string? Shorten(string? text)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }

    private static AIException NotAnApi(Exception? inner) =>
        new(AIErrorKind.NotAnApi, "The server's answer is not in the OpenAI format.", innerException: inner);
}

/// <summary>
/// Creates providers that share one connection pool, refusing remote addresses the user has not allowed.
/// </summary>
internal sealed class AIProviderFactory : IAIProviderFactory, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // Answers can take minutes; the provider watches for silence instead.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public IAIProvider Create(AIConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (AIProviders.ParseUrl(connection.BaseUrl) is null)
        {
            throw new AIException(AIErrorKind.InvalidUrl, $"'{connection.BaseUrl}' is not an http(s) address.");
        }

        if (connection.IsRemote && !connection.AllowRemote)
        {
            throw new AIException(AIErrorKind.RemoteNotAllowed, $"Sending records to '{connection.BaseUrl}' has not been allowed.");
        }

        return new OpenAICompatibleProvider(_http, connection);
    }

    public void Dispose() => _http.Dispose();
}
