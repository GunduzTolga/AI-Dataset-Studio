using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIDatasetStudio.Application.AI;

namespace AIDatasetStudio.Infrastructure.AI;

/// <summary>
/// What every model API client shares: the address, the API key, requests that give up when the
/// server stays silent, and errors told apart by status and the server's own explanation.
/// </summary>
internal abstract class HttpModelProvider : IAIProvider
{
    /// <summary>
    /// How long the server may stay silent before generation is given up: long enough for a large
    /// model to load on a CPU, short enough that a hung server is noticed.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private const int MaxDetailLength = 300;

    private readonly HttpClient _http;

    /// <param name="apiKey">The key to send, resolved by the factory; <c>null</c> sends none.</param>
    protected HttpModelProvider(HttpClient http, AIConnection connection, string? apiKey, TimeSpan? idleTimeout)
    {
        _http = http;
        Connection = connection;
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        BaseUrl = AIProviders.ParseUrl(connection.BaseUrl)
            ?? throw new AIException(AIErrorKind.InvalidUrl, $"'{connection.BaseUrl}' is not an http(s) address.");
        Idle = idleTimeout ?? IdleTimeout;
    }

    public AIConnection Connection { get; }

    protected string? ApiKey { get; }

    protected Uri BaseUrl { get; }

    protected TimeSpan Idle { get; }

    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    public abstract IAsyncEnumerable<string> StreamChatAsync(AIChatRequest request, CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the API key the way the service expects it.
    /// </summary>
    protected abstract void Authorize(HttpRequestMessage request, string apiKey);

    protected HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + path));
        if (ApiKey is { } key)
        {
            Authorize(request, key);
        }

        return request;
    }

    protected Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken, cancellationToken);

    /// <param name="token">Cancels the request, also when the server is silent too long.</param>
    /// <param name="userToken">Cancelled only by the caller; tells a stop from a timeout.</param>
    /// <param name="embedding">Whether the embedding model is asked for, rather than the chat model.</param>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, CancellationToken userToken, bool embedding = false)
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
                HttpStatusCode.NotFound when embedding && detail?.Contains("model", StringComparison.OrdinalIgnoreCase) == true =>
                    new AIException(AIErrorKind.EmbeddingModelNotFound, $"The server has no embedding model '{Connection.EmbeddingModel}'.", detail),
                HttpStatusCode.NotFound when detail?.Contains("model", StringComparison.OrdinalIgnoreCase) == true =>
                    new AIException(AIErrorKind.ModelNotFound, $"The server has no model '{Connection.Model}'.", detail),
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                    new AIException(AIErrorKind.NotAnApi, $"'{request.RequestUri}' is not the expected API.", detail),
                _ when (int)response.StatusCode < 500 =>
                    new AIException(AIErrorKind.Rejected, $"The server refused the request ({(int)response.StatusCode}).", detail),
                _ => new AIException(AIErrorKind.ServerError, $"The server failed ({(int)response.StatusCode}).", detail),
            };
        }
    }

    protected Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        ReadBodyAsync(response, cancellationToken, cancellationToken);

    protected async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token, CancellationToken userToken)
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

    /// <summary>
    /// The next line of an event stream; each line restarts the silence timer.
    /// </summary>
    protected async Task<string?> ReadLineAsync(StreamReader reader, CancellationTokenSource idle, CancellationToken userToken)
    {
        try
        {
            var line = await reader.ReadLineAsync(idle.Token);
            idle.CancelAfter(Idle);
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
        OperationCanceledException => new AIException(AIErrorKind.Timeout, $"'{BaseUrl}' stopped answering.", innerException: exception),
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError }
            or HttpRequestException { InnerException: SocketException } =>
            new AIException(AIErrorKind.Unreachable, $"Nothing answers at '{BaseUrl}'.", exception.Message, exception),
        HttpRequestException or IOException => new AIException(AIErrorKind.ServerError, $"The connection to '{BaseUrl}' failed.", exception.Message, exception),
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

    // OpenAI and Anthropic: {"error": {"message": "…"}}; Ollama's native API: {"error": "…"}.
    protected static string? ErrorText(JsonNode error) => Shorten(error.GetValueKind() == JsonValueKind.String
        ? error.GetValue<string>()
        : error["message"]?.GetValueKind() == JsonValueKind.String ? error["message"]!.GetValue<string>() : error.ToJsonString());

    private static string? Shorten(string? text)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }

    protected static AIException NotAnApi(Exception? inner) =>
        new(AIErrorKind.NotAnApi, "The server's answer is not in the expected format.", innerException: inner);
}
