using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AIDatasetStudio.Application.AI;

namespace AIDatasetStudio.Desktop.Tests.Support;

/// <summary>
/// Model servers for tests: answers are scripted, requests kept, and an answer can be held
/// half-way until the test lets it go on.
/// </summary>
internal sealed class FakeAIProviders : IAIProviderFactory
{
    public IReadOnlyList<string> Models { get; set; } = ["qwen2.5:7b", "llama3.2"];

    /// <summary>
    /// The pieces of the next answers; "Tamam." when none is queued.
    /// </summary>
    public Queue<string[]> Answers { get; } = new();

    /// <summary>
    /// Thrown by the next request instead of answering.
    /// </summary>
    public AIException? Failure { get; set; }

    /// <summary>
    /// When set, an answer stops after its first piece until this completes.
    /// </summary>
    public TaskCompletionSource? Hold { get; set; }

    public ConcurrentQueue<(AIConnection Connection, AIChatRequest Request)> Requests { get; } = new();

    /// <summary>
    /// Turns a text into its vector; by default the words of the text, so texts sharing most
    /// words are close.
    /// </summary>
    public Func<string, float[]> Embedder { get; set; } = WordVector;

    /// <summary>
    /// The texts sent to be embedded, one list per request.
    /// </summary>
    public ConcurrentQueue<IReadOnlyList<string>> Embedded { get; } = new();

    /// <summary>
    /// A vector counting the words of a text, each word in one of 64 dimensions.
    /// </summary>
    public static float[] WordVector(string text)
    {
        var vector = new float[64];
        foreach (var word in text.ToLowerInvariant().Split([' ', '?', '.', ',', '!', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = 2166136261u;
            foreach (var c in word)
            {
                hash = (hash ^ c) * 16777619u;
            }

            vector[hash % 64]++;
        }

        return vector;
    }

    public IAIProvider Create(AIConnection connection)
    {
        if (AIProviders.ParseUrl(connection.BaseUrl) is null)
        {
            throw new AIException(AIErrorKind.InvalidUrl, "invalid");
        }

        if (connection.IsRemote && !connection.AllowRemote)
        {
            throw new AIException(AIErrorKind.RemoteNotAllowed, "remote");
        }

        return new Provider(this, connection);
    }

    private sealed class Provider(FakeAIProviders owner, AIConnection connection) : IAIProvider
    {
        public AIConnection Connection => connection;

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            owner.Failure is { } failure ? Task.FromException<IReadOnlyList<string>>(failure) : Task.FromResult(owner.Models);

        public async IAsyncEnumerable<string> StreamChatAsync(AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            owner.Requests.Enqueue((connection, request));
            if (owner.Failure is { } failure)
            {
                throw failure;
            }

            var pieces = owner.Answers.TryDequeue(out var scripted) ? scripted : ["Tamam."];
            for (var i = 0; i < pieces.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return pieces[i];
                if (i == 0 && owner.Hold is { } hold)
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }

                await Task.Yield();
            }
        }

        public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
        {
            owner.Embedded.Enqueue([.. inputs]);
            if (owner.Failure is { } failure)
            {
                throw failure;
            }

            if (string.IsNullOrWhiteSpace(connection.EmbeddingModel))
            {
                throw new AIException(AIErrorKind.NoEmbeddingModel, "no embedding model");
            }

            if (owner.Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return [.. inputs.Select(owner.Embedder)];
        }
    }
}
