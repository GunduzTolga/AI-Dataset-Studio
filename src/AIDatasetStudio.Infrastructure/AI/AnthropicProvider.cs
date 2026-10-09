using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Infrastructure.AI;

/// <summary>
/// Talks to Anthropic's Messages API (M19): system prompts go apart from the conversation, an
/// answer length is always given, and the answer streams as typed events of which only text
/// deltas are kept (thinking is left out, like reasoning elsewhere). Anthropic offers no
/// embeddings.
/// </summary>
internal sealed class AnthropicProvider(HttpClient http, AIConnection connection, string? apiKey = null, TimeSpan? idleTimeout = null)
    : HttpModelProvider(http, connection, apiKey, idleTimeout)
{
    /// <summary>
    /// The API version the requests are written for.
    /// </summary>
    internal const string ApiVersion = "2023-06-01";

    // The Messages API needs a length; this is the app's own default.
    private const int DefaultMaxTokens = AIConnection.DefaultMaxTokens;

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "models?limit=1000");
        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);
        try
        {
            // Newest first, as the API lists them.
            var models = JsonNode.Parse(body)?["data"]?.AsArray()
                .Select(model => model?["id"]?.GetValue<string>())
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return models ?? throw NotAnApi(null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw NotAnApi(ex);
        }
    }

    public override async IAsyncEnumerable<string> StreamChatAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var body = new JsonObject
        {
            ["model"] = Connection.Model,
            ["max_tokens"] = request.MaxTokens ?? DefaultMaxTokens,
            ["messages"] = new JsonArray([.. request.Messages.Where(m => m.Role != ChatRole.System).Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = m.Content,
            })]),
            ["stream"] = true,

            // Anthropic takes 0 to 1; the app's scale goes to 2.
            ["temperature"] = Math.Clamp(request.Temperature, 0, 1),
        };
        if (system.Length > 0)
        {
            body["system"] = system;
        }

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(Idle);
        using var message = Request(HttpMethod.Post, "messages");
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await SendAsync(message, idle.Token, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
        {
            // A whole message: {"content": [{"type": "text", "text": "…"}]}.
            var whole = await ReadBodyAsync(response, idle.Token, cancellationToken);
            yield return WholeText(whole);
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

            var (text, done) = Event(line[5..].Trim());
            if (text is { Length: > 0 })
            {
                yield return text;
            }

            if (done)
            {
                yield break;
            }
        }
    }

    public override Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default) =>
        throw new AIException(AIErrorKind.NoEmbeddingModel, "Anthropic offers no embedding models.");

    protected override void Authorize(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
    }

    /// <summary>
    /// The text of one streamed event, and whether the message is over.
    /// </summary>
    private static (string? Text, bool Done) Event(string json)
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

        switch (node?["type"]?.GetValueKind() == JsonValueKind.String ? node["type"]!.GetValue<string>() : null)
        {
            case "content_block_delta":
                var delta = node!["delta"];
                return delta?["type"]?.GetValue<string>() == "text_delta" && delta["text"]?.GetValueKind() == JsonValueKind.String
                    ? (delta["text"]!.GetValue<string>(), false)
                    : (null, false);
            case "message_stop":
                return (null, true);
            case "error":
                throw new AIException(AIErrorKind.ServerError, "The server reported an error while answering.", node!["error"] is { } error ? ErrorText(error) : null);
            default:
                // message_start, content_block_start/stop, message_delta, ping.
                return (null, false);
        }
    }

    private static string WholeText(string json)
    {
        try
        {
            var content = JsonNode.Parse(json)?["content"]?.AsArray() ?? throw NotAnApi(null);
            return string.Concat(content
                .Where(block => block?["type"]?.GetValue<string>() == "text")
                .Select(block => block?["text"]?.GetValue<string>()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw NotAnApi(ex);
        }
    }
}
