using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Infrastructure.AI;

/// <summary>
/// Talks to a server with the OpenAI chat completions API: Ollama, LM Studio, vLLM, LocalAI,
/// OpenAI, Azure OpenAI, OpenRouter, Groq, DeepSeek and others. Answers are streamed as
/// server-sent events; a server that answers in one piece works too. Texts are turned into
/// vectors with the same server's embeddings API.
/// </summary>
internal sealed class OpenAICompatibleProvider(HttpClient http, AIConnection connection, string? apiKey = null, TimeSpan? idleTimeout = null)
    : HttpModelProvider(http, connection, apiKey, idleTimeout)
{
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
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

    public override async IAsyncEnumerable<string> StreamChatAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            // OpenAI's reasoning models take only the newer name; other servers know the older one.
            body[Connection.Kind is AIProviderKind.OpenAI or AIProviderKind.AzureOpenAI ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        }

        // Silence is measured from the request until each piece of the answer.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(Idle);
        HttpResponseMessage sent;
        try
        {
            sent = await SendChatAsync(body, idle.Token, cancellationToken);
        }
        catch (AIException ex) when (ex.Kind == AIErrorKind.Rejected && ex.Detail?.Contains("temperature", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Reasoning models (OpenAI o-series, GPT-5) take only their own temperature.
            body.Remove("temperature");
            sent = await SendChatAsync(body, idle.Token, cancellationToken);
        }

        using var response = sent;
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

    public override async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (string.IsNullOrWhiteSpace(Connection.EmbeddingModel))
        {
            throw new AIException(AIErrorKind.NoEmbeddingModel, "No embedding model is chosen for this connection.");
        }

        if (inputs.Count == 0)
        {
            return [];
        }

        var body = new JsonObject
        {
            ["model"] = Connection.EmbeddingModel.Trim(),
            ["input"] = new JsonArray([.. inputs.Select(input => (JsonNode?)JsonValue.Create(input))]),
        };
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(Idle);
        using var message = Request(HttpMethod.Post, "embeddings");
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await SendAsync(message, idle.Token, cancellationToken, embedding: true);
        var answer = await ReadBodyAsync(response, idle.Token, cancellationToken);
        try
        {
            var node = JsonNode.Parse(answer);
            if (node?["error"] is { } error)
            {
                throw new AIException(AIErrorKind.ServerError, "The server reported an error while embedding.", ErrorText(error));
            }

            // Each vector carries its input's index; servers may send them in another order.
            var vectors = new float[]?[inputs.Count];
            var data = node?["data"]?.AsArray() ?? throw NotAnApi(null);
            for (var i = 0; i < data.Count; i++)
            {
                var item = data[i] ?? throw NotAnApi(null);
                var index = item["index"]?.GetValue<int>() ?? i;
                if (index < 0 || index >= vectors.Length || item["embedding"] is not JsonArray values || values.Count == 0)
                {
                    throw NotAnApi(null);
                }

                vectors[index] = [.. values.Select(value => value?.GetValue<float>() ?? throw NotAnApi(null))];
            }

            return [.. vectors.Select(vector => vector ?? throw NotAnApi(null))];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw NotAnApi(ex);
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

    protected override void Authorize(HttpRequestMessage request, string apiKey)
    {
        if (Connection.Kind == AIProviderKind.AzureOpenAI)
        {
            request.Headers.Add("api-key", apiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    private async Task<HttpResponseMessage> SendChatAsync(JsonObject body, CancellationToken token, CancellationToken userToken)
    {
        using var message = Request(HttpMethod.Post, "chat/completions");
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await SendAsync(message, token, userToken);
    }

}
