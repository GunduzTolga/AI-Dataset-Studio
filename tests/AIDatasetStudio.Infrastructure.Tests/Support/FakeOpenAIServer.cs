using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIDatasetStudio.Infrastructure.Tests.Support;

/// <summary>
/// A local HTTP server speaking the OpenAI chat completions API the way Ollama and LM Studio do:
/// <c>GET /v1/models</c> and streamed <c>POST /v1/chat/completions</c>. Answers are scripted per
/// test; every request body is kept for assertions.
/// </summary>
internal sealed class FakeOpenAIServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public FakeOpenAIServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

    public IReadOnlyList<string> Models { get; set; } = ["qwen2.5:7b", "llama3.2"];

    /// <summary>
    /// The pieces of the next answers, streamed one event each.
    /// </summary>
    public Queue<string[]> Answers { get; } = new();

    /// <summary>
    /// Pause between pieces, so a test can watch an answer arrive.
    /// </summary>
    public TimeSpan PieceDelay { get; set; }

    /// <summary>
    /// The status and body to answer every request with instead of the script.
    /// </summary>
    public (HttpStatusCode Status, string Body)? Failure { get; set; }

    public ConcurrentQueue<JsonNode> Requests { get; } = new();

    public ConcurrentQueue<string?> Authorizations { get; } = new();

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            Authorizations.Enqueue(context.Request.Headers["Authorization"]);
            if (Failure is { } failure)
            {
                await WriteAsync(response, failure.Status, "application/json", failure.Body);
                return;
            }

            var path = context.Request.Url!.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path == "/v1/models")
            {
                var data = new JsonArray([.. Models.Select(m => (JsonNode)new JsonObject { ["id"] = m, ["object"] = "model" })]);
                await WriteAsync(response, HttpStatusCode.OK, "application/json", new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString());
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/v1/chat/completions")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
                Requests.Enqueue(body);
                var pieces = Answers.TryDequeue(out var scripted) ? scripted : ["Tamam."];
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                response.SendChunked = true;
                await using var stream = response.OutputStream;
                await SendAsync(stream, new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["role"] = "assistant" } }) });
                foreach (var piece in pieces)
                {
                    if (PieceDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(PieceDelay);
                    }

                    await SendAsync(stream, new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = piece } }) });
                }

                await stream.WriteAsync("data: [DONE]\n\n"u8.ToArray());
                return;
            }

            await WriteAsync(response, HttpStatusCode.NotFound, "text/plain", "404 page not found");
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The client went away (cancelled generation).
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private static async Task SendAsync(Stream stream, JsonNode data)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {data.ToJsonString()}\n\n"));
        await stream.FlushAsync();
    }

    private static async Task WriteAsync(HttpListenerResponse response, HttpStatusCode status, string contentType, string body)
    {
        response.StatusCode = (int)status;
        response.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
