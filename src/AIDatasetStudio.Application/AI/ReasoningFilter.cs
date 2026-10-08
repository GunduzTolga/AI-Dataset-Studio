using System.Runtime.CompilerServices;
using System.Text;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// Removes the "&lt;think&gt;…&lt;/think&gt;" blocks reasoning models write before their answer,
/// from a stream whose pieces may split a tag anywhere, and the white space that opens the answer.
/// </summary>
public sealed class ReasoningFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _thinking;
    private bool _started;

    /// <summary>
    /// The text of <paramref name="source"/> without reasoning blocks.
    /// </summary>
    public static async IAsyncEnumerable<string> ApplyAsync(IAsyncEnumerable<string> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var filter = new ReasoningFilter();
        await foreach (var piece in source.WithCancellation(cancellationToken))
        {
            if (filter.Push(piece) is { Length: > 0 } text)
            {
                yield return text;
            }
        }

        if (filter.Flush() is { Length: > 0 } rest)
        {
            yield return rest;
        }
    }

    /// <summary>
    /// Takes the next piece and returns the answer text that is certain so far.
    /// </summary>
    public string Push(string piece)
    {
        _pending.Append(piece);
        var output = new StringBuilder();
        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tag = _thinking ? Close : Open;
            var index = text.IndexOf(tag, StringComparison.Ordinal);
            if (index >= 0)
            {
                if (!_thinking)
                {
                    output.Append(text, 0, index);
                }

                _pending.Remove(0, index + tag.Length);
                _thinking = !_thinking;
                continue;
            }

            // Keep a tail that may be the start of a tag split across pieces.
            var keep = PartialTagLength(text, tag);
            if (!_thinking)
            {
                output.Append(text, 0, text.Length - keep);
            }

            _pending.Remove(0, text.Length - keep);
            break;
        }

        return Started(output.ToString());
    }

    /// <summary>
    /// The text held back at the end of the stream; an unclosed reasoning block is dropped.
    /// </summary>
    public string Flush()
    {
        var rest = _thinking ? string.Empty : _pending.ToString();
        _pending.Clear();
        return Started(rest);
    }

    private static int PartialTagLength(string text, string tag)
    {
        for (var length = Math.Min(tag.Length - 1, text.Length); length > 0; length--)
        {
            if (text.AsSpan(text.Length - length).SequenceEqual(tag.AsSpan(0, length)))
            {
                return length;
            }
        }

        return 0;
    }

    private string Started(string text)
    {
        if (_started)
        {
            return text;
        }

        text = text.TrimStart();
        _started = text.Length > 0;
        return text;
    }
}
