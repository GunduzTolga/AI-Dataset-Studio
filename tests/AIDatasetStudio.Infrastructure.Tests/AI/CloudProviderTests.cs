using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Infrastructure.AI;

namespace AIDatasetStudio.Infrastructure.Tests.AI;

/// <summary>
/// Cloud model services (M19, SDD §17.1): how each is addressed, and Anthropic's Messages API.
/// </summary>
public sealed class CloudProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AIChatRequest Hello => new(
        [new(ChatRole.System, "Kısa yanıt ver."), new(ChatRole.System, "Türkçe yaz."), new(ChatRole.User, "Merhaba!")], 1.4, null);

    [Fact]
    public void Cloud_services_have_their_addresses_keys_and_need_consent()
    {
        Assert.Equal("https://api.openai.com/v1", AIProviders.DefaultUrl(AIProviderKind.OpenAI));
        Assert.Equal("https://api.anthropic.com/v1", AIProviders.DefaultUrl(AIProviderKind.Anthropic));
        Assert.Equal("https://openrouter.ai/api/v1", AIProviders.DefaultUrl(AIProviderKind.OpenRouter));
        Assert.Equal("https://api.groq.com/openai/v1", AIProviders.DefaultUrl(AIProviderKind.Groq));
        Assert.Equal("https://api.deepseek.com/v1", AIProviders.DefaultUrl(AIProviderKind.DeepSeek));

        // Azure's address names the user's resource, so there is none to start with.
        Assert.Null(AIProviders.ParseUrl(AIProviders.DefaultUrl(AIProviderKind.AzureOpenAI)));

        var cloud = Enum.GetValues<AIProviderKind>().Where(AIProviders.IsCloud).ToList();
        Assert.Equal(6, cloud.Count);
        Assert.All(cloud, kind => Assert.NotNull(AIProviders.KeyVariable(kind)));
        Assert.All(cloud.Where(k => k != AIProviderKind.AzureOpenAI), kind => Assert.False(AIProviders.IsLocal(AIProviders.DefaultUrl(kind))));
        Assert.Null(AIProviders.KeyVariable(AIProviderKind.Ollama));
        Assert.Equal(
            [AIProviderKind.Anthropic, AIProviderKind.Groq, AIProviderKind.DeepSeek],
            Enum.GetValues<AIProviderKind>().Where(k => !AIProviders.OffersEmbeddings(k)));

        using var factory = new AIProviderFactory();
        var openAI = new AIConnection(Guid.CreateVersion7(), "OpenAI", AIProviderKind.OpenAI, AIProviders.DefaultUrl(AIProviderKind.OpenAI), "gpt-4.1-mini", ApiKey: "sk-test");
        Assert.Equal(AIErrorKind.RemoteNotAllowed, Assert.Throws<AIException>(() => factory.Create(openAI)).Kind);
        Assert.IsType<OpenAICompatibleProvider>(factory.Create(openAI with { AllowRemote = true }));
        Assert.IsType<AnthropicProvider>(factory.Create(openAI with { Kind = AIProviderKind.Anthropic, BaseUrl = AIProviders.DefaultUrl(AIProviderKind.Anthropic), AllowRemote = true }));
    }

    [Fact]
    public async Task OpenAI_and_Azure_get_the_key_and_the_answer_length_the_way_they_expect()
    {
        var requests = new List<(HttpRequestMessage Request, JsonNode Body)>();
        var http = Recording(requests, _ => Respond(HttpStatusCode.OK, """{"choices":[{"message":{"content":"Tamam."}}]}"""));
        var request = Hello with { MaxTokens = 64 };

        await CollectAsync(new OpenAICompatibleProvider(http, Connection(AIProviderKind.OpenAI), "sk-openai"), request);
        await CollectAsync(new OpenAICompatibleProvider(http, Connection(AIProviderKind.AzureOpenAI), "azure-key"), request);
        await CollectAsync(new OpenAICompatibleProvider(http, Connection(AIProviderKind.Groq), "gsk-groq"), request);

        Assert.Equal("Bearer sk-openai", requests[0].Request.Headers.Authorization?.ToString());
        Assert.Equal((64, null), ((int?)requests[0].Body["max_completion_tokens"], (int?)requests[0].Body["max_tokens"]));
        Assert.Null(requests[1].Request.Headers.Authorization);
        Assert.Equal(["azure-key"], requests[1].Request.Headers.GetValues("api-key"));
        Assert.Equal(64, (int?)requests[1].Body["max_completion_tokens"]);
        Assert.Equal((null, 64), ((int?)requests[2].Body["max_completion_tokens"], (int?)requests[2].Body["max_tokens"]));
    }

    [Fact]
    public async Task A_model_that_takes_only_its_own_temperature_is_asked_again_without_one()
    {
        var requests = new List<(HttpRequestMessage Request, JsonNode Body)>();
        var http = Recording(requests, body => body["temperature"] is null
            ? Respond(HttpStatusCode.OK, """{"choices":[{"message":{"content":"Tamam."}}]}""")
            : Respond(HttpStatusCode.BadRequest, """{"error":{"message":"Unsupported value: 'temperature' does not support 1.4 with this model. Only the default (1) value is supported.","type":"invalid_request_error","param":"temperature","code":"unsupported_value"}}"""));

        Assert.Equal(["Tamam."], await CollectAsync(new OpenAICompatibleProvider(http, Connection(AIProviderKind.OpenAI), "sk-openai"), Hello));

        Assert.Equal([1.4, null], requests.Select(r => (double?)r.Body["temperature"]));

        // Other refusals are reported as they are.
        var refused = Recording([], _ => Respond(HttpStatusCode.BadRequest, """{"error":{"message":"context_length_exceeded"}}"""));
        var error = await Assert.ThrowsAsync<AIException>(() => CollectAsync(new OpenAICompatibleProvider(refused, Connection(AIProviderKind.OpenAI), "sk"), Hello));
        Assert.Equal((AIErrorKind.Rejected, "context_length_exceeded"), (error.Kind, error.Detail));
    }

    [Fact]
    public async Task Anthropic_streams_text_deltas_with_the_system_prompt_apart()
    {
        var events = string.Concat(
            Event("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[]}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Kullanıcı selam veriyor."}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
            Event("ping", """{"type": "ping"}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Mer"}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"haba!"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));
        var requests = new List<(HttpRequestMessage Request, JsonNode Body)>();
        var provider = new AnthropicProvider(Recording(requests, _ => Respond(HttpStatusCode.OK, events, "text/event-stream")), Connection(AIProviderKind.Anthropic), "sk-ant-test");

        Assert.Equal(["Mer", "haba!"], await CollectAsync(provider, Hello));

        var (request, body) = Assert.Single(requests);
        Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
        Assert.Equal(["sk-ant-test"], request.Headers.GetValues("x-api-key"));
        Assert.Equal([AnthropicProvider.ApiVersion], request.Headers.GetValues("anthropic-version"));
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("Kısa yanıt ver.\n\nTürkçe yaz.", (string?)body["system"]);
        Assert.Equal(["user"], body["messages"]!.AsArray().Select(m => (string?)m!["role"]));
        Assert.Equal(AIConnection.DefaultMaxTokens, (int?)body["max_tokens"]);
        Assert.Equal(1.0, (double?)body["temperature"]);
        Assert.True((bool?)body["stream"]);
    }

    [Fact]
    public async Task Anthropic_lists_its_models_and_its_errors_are_explained()
    {
        var models = new AnthropicProvider(Recording([], _ => Respond(HttpStatusCode.OK, """
            {"data":[{"type":"model","id":"claude-sonnet-4-5","display_name":"Claude Sonnet 4.5"},{"type":"model","id":"claude-haiku-4-5","display_name":"Claude Haiku 4.5"}],"has_more":false}
            """)), Connection(AIProviderKind.Anthropic), "sk-ant");
        Assert.Equal(["claude-sonnet-4-5", "claude-haiku-4-5"], await models.ListModelsAsync(Ct));

        var whole = new AnthropicProvider(Recording([], _ => Respond(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Tek parça."}]}""")), Connection(AIProviderKind.Anthropic), "sk-ant");
        Assert.Equal(["Tek parça."], await CollectAsync(whole, Hello));

        var midway = new AnthropicProvider(Recording([], _ => Respond(HttpStatusCode.OK,
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Ya"}}""")
                + Event("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""), "text/event-stream")), Connection(AIProviderKind.Anthropic), "sk-ant");
        var overloaded = await Assert.ThrowsAsync<AIException>(() => CollectAsync(midway, Hello));
        Assert.Equal((AIErrorKind.ServerError, "Overloaded"), (overloaded.Kind, overloaded.Detail));

        var badKey = new AnthropicProvider(Recording([], _ => Respond(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""")), Connection(AIProviderKind.Anthropic), "sk-ant");
        Assert.Equal((AIErrorKind.Unauthorized, "invalid x-api-key"), await KindAndDetail(badKey));

        var noModel = new AnthropicProvider(Recording([], _ => Respond(HttpStatusCode.NotFound, """{"type":"error","error":{"type":"not_found_error","message":"model: claude-yok"}}""")), Connection(AIProviderKind.Anthropic), "sk-ant");
        Assert.Equal((AIErrorKind.ModelNotFound, "model: claude-yok"), await KindAndDetail(noModel));

        Assert.Equal(AIErrorKind.NoEmbeddingModel, (await Assert.ThrowsAsync<AIException>(() => whole.EmbedAsync(["a"], Ct))).Kind);
    }

    private static async Task<(AIErrorKind, string?)> KindAndDetail(IAIProvider provider)
    {
        var error = await Assert.ThrowsAsync<AIException>(() => CollectAsync(provider, Hello));
        return (error.Kind, error.Detail);
    }

    private static AIConnection Connection(AIProviderKind kind) =>
        new(Guid.CreateVersion7(), kind.ToString(), kind, kind == AIProviderKind.AzureOpenAI ? "https://studio.openai.azure.com/openai/v1" : AIProviders.DefaultUrl(kind), "model-a", AllowRemote: true);

    private static async Task<List<string>> CollectAsync(IAIProvider provider, AIChatRequest request)
    {
        var pieces = new List<string>();
        await foreach (var piece in provider.StreamChatAsync(request, Ct))
        {
            pieces.Add(piece);
        }

        return pieces;
    }

    private static string Event(string name, string data) => $"event: {name}\ndata: {data}\n\n";

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpClient Recording(List<(HttpRequestMessage Request, JsonNode Body)> requests, Func<JsonNode, HttpResponseMessage> respond) =>
        new(new Handler(async request =>
        {
            var body = request.Content is null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync()) ?? new JsonObject();
            requests.Add((request, body));
            return respond(body);
        }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
