using System.Collections.Concurrent;
using AIDatasetStudio.Application.Analysis;
using Microsoft.ML.Tokenizers;

namespace AIDatasetStudio.Infrastructure.Analysis;

/// <summary>
/// OpenAI's BPE encodings from Microsoft.ML.Tokenizers, with the vocabularies shipped in NuGet
/// data packages, so counting works offline (SDD §7.2). Loading a vocabulary takes a moment,
/// so each tokenizer is created once and shared; they are thread-safe.
/// </summary>
internal sealed class TiktokenProvider : ITokenizerProvider
{
    private readonly ConcurrentDictionary<string, Lazy<ITokenCounter>> _tokenizers = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Available { get; } = [Tokenizers.O200kBase, Tokenizers.Cl100kBase];

    public ITokenCounter Get(string tokenizerId)
    {
        if (!Available.Contains(tokenizerId, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown tokenizer '{tokenizerId}'.", nameof(tokenizerId));
        }

        return _tokenizers.GetOrAdd(tokenizerId, id => new Lazy<ITokenCounter>(() => new Counter(TiktokenTokenizer.CreateForEncoding(id)))).Value;
    }

    private sealed class Counter(Tokenizer tokenizer) : ITokenCounter
    {
        public int CountTokens(string text) => string.IsNullOrEmpty(text) ? 0 : tokenizer.CountTokens(text);
    }
}
