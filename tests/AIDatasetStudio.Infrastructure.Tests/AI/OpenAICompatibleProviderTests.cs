using System.Net;
using System.Net.Sockets;
using System.Text;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Infrastructure.AI;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.AI;

/// <summary>
/// The OpenAI-compatible client (M12, SDD §17) against a local server and scripted responses.
/// </summary>
public sealed class OpenAICompatibleProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AIChatRequest Hello => new([new(ChatRole.System, "Kısa yanıt ver."), new(ChatRole.User, "Merhaba!")], 0.2, 256);

    [Fact]
    public async Task Lists_the_models_of_the_server()
    {
        await using var server = new FakeOpenAIServer();
        using var factory = new AIProviderFactory();
        var provider = factory.Create(Connection(server.BaseUrl + "/", apiKey: "gizli"));

        var models = await provider.ListModelsAsync(Ct);

        Assert.Equal(["llama3.2", "qwen2.5:7b"], models);
        Assert.Equal("Bearer gizli", Assert.Single(server.Authorizations));
    }

    [Fact]
    public async Task Streams_the_answer_as_it_is_written()
    {
        await using var server = new FakeOpenAIServer { PieceDelay = TimeSpan.FromMilliseconds(30) };
        server.Answers.Enqueue(["Mer", "haba", "! Nasıl yardımcı olabilirim?"]);
        using var factory = new AIProviderFactory();
        var provider = factory.Create(Connection(server.BaseUrl));

        var pieces = new List<string>();
        await foreach (var piece in provider.StreamChatAsync(Hello, Ct))
        {
            pieces.Add(piece);
        }

        Assert.Equal(["Mer", "haba", "! Nasıl yardımcı olabilirim?"], pieces);
        var body = Assert.Single(server.Requests);
        Assert.Equal("qwen2.5:7b", (string?)body["model"]);
        Assert.True((bool)body["stream"]!);
        Assert.Equal(0.2, (double)body["temperature"]!);
        Assert.Equal(256, (int)body["max_tokens"]!);
        Assert.Equal(["system", "user"], body["messages"]!.AsArray().Select(m => (string?)m!["role"]));
        Assert.Equal("Merhaba!", (string?)body["messages"]![1]!["content"]);
        Assert.Null(Assert.Single(server.Authorizations));
    }

    [Fact]
    public async Task Stopping_cancels_the_answer()
    {
        await using var server = new FakeOpenAIServer { PieceDelay = TimeSpan.FromMilliseconds(200) };
        server.Answers.Enqueue([.. Enumerable.Range(0, 50).Select(i => $"{i} ")]);
        using var factory = new AIProviderFactory();
        var provider = factory.Create(Connection(server.BaseUrl));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(Hello, stop.Token))
            {
                if (++received == 2)
                {
                    await stop.CancelAsync();
                }
            }
        });

        Assert.Equal(2, received);
    }

    [Fact]
    public async Task Reasoning_and_role_events_are_not_part_of_the_answer()
    {
        var events = string.Concat(
            Event("""{"choices":[{"delta":{"role":"assistant"}}]}"""),
            Event("""{"choices":[{"delta":{"reasoning_content":"Kullanıcı selam veriyor."}}]}"""),
            Event("""{"choices":[{"delta":{"content":"Selam"},"finish_reason":null}]}"""),
            ": keep-alive comment\n\n",
            Event("""{"choices":[{"delta":{"content":"!"},"finish_reason":"stop"}]}"""),
            Event("[DONE]"));
        var provider = Stubbed(_ => Respond(HttpStatusCode.OK, events, "text/event-stream"));

        Assert.Equal(["Selam", "!"], await CollectAsync(provider));
    }

    [Fact]
    public async Task A_server_that_ignores_streaming_answers_in_one_piece()
    {
        var provider = Stubbed(_ => Respond(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Tek parça yanıt."}}]}"""));

        Assert.Equal(["Tek parça yanıt."], await CollectAsync(provider));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API key"}}""", AIErrorKind.Unauthorized, "Invalid API key")]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"message":"model \"qwen2.5:7b\" not found, try pulling it first","type":"api_error"}}""", AIErrorKind.ModelNotFound, "model \"qwen2.5:7b\" not found, try pulling it first")]
    [InlineData(HttpStatusCode.NotFound, "404 page not found", AIErrorKind.NotAnApi, "404 page not found")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"context length exceeded"}""", AIErrorKind.Rejected, "context length exceeded")]
    [InlineData(HttpStatusCode.InternalServerError, "", AIErrorKind.ServerError, null)]
    public async Task Server_errors_are_explained(HttpStatusCode status, string body, AIErrorKind kind, string? detail)
    {
        await using var server = new FakeOpenAIServer { Failure = (status, body) };
        using var factory = new AIProviderFactory();
        var provider = factory.Create(Connection(server.BaseUrl));

        var error = await Assert.ThrowsAsync<AIException>(() => CollectAsync(provider));

        Assert.Equal((kind, detail), (error.Kind, error.Detail));
    }

    [Fact]
    public async Task An_error_in_the_middle_of_an_answer_is_reported()
    {
        var events = Event("""{"choices":[{"delta":{"content":"Yar"}}]}""") + Event("""{"error":{"message":"out of memory"}}""");
        var provider = Stubbed(_ => Respond(HttpStatusCode.OK, events, "text/event-stream"));

        var error = await Assert.ThrowsAsync<AIException>(() => CollectAsync(provider));

        Assert.Equal((AIErrorKind.ServerError, "out of memory"), (error.Kind, error.Detail));
    }

    [Fact]
    public async Task Nothing_listening_is_unreachable()
    {
        int port;
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        }

        using var factory = new AIProviderFactory();
        var provider = factory.Create(Connection($"http://127.0.0.1:{port}/v1"));

        Assert.Equal(AIErrorKind.Unreachable, (await Assert.ThrowsAsync<AIException>(() => provider.ListModelsAsync(Ct))).Kind);
    }

    [Fact]
    public async Task A_silent_server_times_out()
    {
        var silent = new SilentContent();
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = silent })));
        var provider = new OpenAICompatibleProvider(http, Connection("http://localhost:11434/v1"), idleTimeout: TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAsync<AIException>(() => CollectAsync(provider));

        Assert.Equal(AIErrorKind.Timeout, error.Kind);
    }

    [Fact]
    public async Task Embeds_texts_with_the_embedding_model_in_input_order()
    {
        string? sent = null;
        string? path = null;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            path = request.RequestUri!.AbsolutePath;
            sent = await request.Content!.ReadAsStringAsync();
            return Respond(HttpStatusCode.OK, """
                {"object": "list", "data": [
                  {"object": "embedding", "index": 1, "embedding": [0.5, -0.25]},
                  {"object": "embedding", "index": 0, "embedding": [1, 2e-3]}
                ]}
                """);
        }));
        var provider = new OpenAICompatibleProvider(http, Connection("http://localhost:11434/v1") with { EmbeddingModel = " nomic-embed-text " });

        var vectors = await provider.EmbedAsync(["Kargo ücreti ne kadar?", "İade süresi nedir?"], Ct);

        Assert.Equal("/v1/embeddings", path);
        var body = System.Text.Json.Nodes.JsonNode.Parse(sent!)!;
        Assert.Equal("nomic-embed-text", (string?)body["model"]);
        Assert.Equal(["Kargo ücreti ne kadar?", "İade süresi nedir?"], body["input"]!.AsArray().Select(i => (string?)i));
        Assert.Equal([1f, 0.002f], vectors[0]);
        Assert.Equal([0.5f, -0.25f], vectors[1]);
        Assert.Empty(await provider.EmbedAsync([], Ct));
    }

    [Fact]
    public async Task Embedding_failures_are_told_apart()
    {
        var noModel = Stubbed(_ => Respond(HttpStatusCode.OK, "{}"));
        Assert.Equal(AIErrorKind.NoEmbeddingModel, (await Assert.ThrowsAsync<AIException>(() => noModel.EmbedAsync(["a"], Ct))).Kind);

        var missing = Embedding(_ => Respond(HttpStatusCode.NotFound, """{"error": {"message": "model \"nomic-embed-text\" not found, try pulling it first"}}"""));
        var error = await Assert.ThrowsAsync<AIException>(() => missing.EmbedAsync(["a"], Ct));
        Assert.Equal(AIErrorKind.EmbeddingModelNotFound, error.Kind);
        Assert.Contains("not found", error.Detail, StringComparison.Ordinal);

        var chatOnly = Embedding(_ => Respond(HttpStatusCode.BadRequest, """{"error": "this model does not support embeddings"}"""));
        Assert.Equal(AIErrorKind.Rejected, (await Assert.ThrowsAsync<AIException>(() => chatOnly.EmbedAsync(["a"], Ct))).Kind);

        var shortAnswer = Embedding(_ => Respond(HttpStatusCode.OK, """{"data": [{"index": 0, "embedding": [1, 2]}]}"""));
        Assert.Equal(AIErrorKind.NotAnApi, (await Assert.ThrowsAsync<AIException>(() => shortAnswer.EmbedAsync(["a", "b"], Ct))).Kind);

        var notNumbers = Embedding(_ => Respond(HttpStatusCode.OK, """{"data": [{"index": 0, "embedding": ["x"]}]}"""));
        Assert.Equal(AIErrorKind.NotAnApi, (await Assert.ThrowsAsync<AIException>(() => notNumbers.EmbedAsync(["a"], Ct))).Kind);
    }

    [Fact]
    public void Remote_servers_need_consent_and_addresses_must_be_http()
    {
        using var factory = new AIProviderFactory();

        Assert.Equal(AIErrorKind.RemoteNotAllowed, Assert.Throws<AIException>(() => factory.Create(Connection("https://api.example.com/v1"))).Kind);
        Assert.NotNull(factory.Create(Connection("https://api.example.com/v1") with { AllowRemote = true }));
        Assert.Equal(AIErrorKind.InvalidUrl, Assert.Throws<AIException>(() => factory.Create(Connection("ftp://localhost/v1"))).Kind);
        Assert.Equal(AIErrorKind.InvalidUrl, Assert.Throws<AIException>(() => factory.Create(Connection("localhost:11434"))).Kind);
        Assert.NotNull(factory.Create(Connection("http://192.168.1.20:1234/v1")));
    }

    private static AIConnection Connection(string url, string? apiKey = null) =>
        new(Guid.CreateVersion7(), "Ollama", AIProviderKind.Ollama, url, "qwen2.5:7b", apiKey);

    private static OpenAICompatibleProvider Stubbed(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(request => Task.FromResult(respond(request)))), Connection("http://localhost:11434/v1"));

    private static OpenAICompatibleProvider Embedding(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(request => Task.FromResult(respond(request)))), Connection("http://localhost:11434/v1") with { EmbeddingModel = "nomic-embed-text" });

    private static async Task<List<string>> CollectAsync(IAIProvider provider)
    {
        var pieces = new List<string>();
        await foreach (var piece in provider.StreamChatAsync(Hello, Ct))
        {
            pieces.Add(piece);
        }

        return pieces;
    }

    private static string Event(string data) => $"data: {data}\n\n";

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    /// <summary>
    /// An event stream that never sends anything.
    /// </summary>
    private sealed class SilentContent : HttpContent
    {
        public SilentContent() => Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.Infinite);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SilentStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
