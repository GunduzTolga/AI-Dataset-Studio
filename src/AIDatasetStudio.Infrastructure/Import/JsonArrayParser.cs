using System.Text.Json;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// Reads the items of a top-level JSON array one at a time, so files larger than memory can
/// be imported. The stream must be UTF-8 without a byte order mark.
/// </summary>
internal sealed class JsonArrayParser(Stream stream)
{
    private const int InitialBufferSize = 64 * 1024;

    private static readonly JsonReaderOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private byte[] _buffer = new byte[InitialBufferSize];
    private int _start;
    private int _end;
    private bool _endOfStream;
    private bool _inArray;
    private bool _finished;
    private JsonReaderState _state = new(Options);

    private enum Step
    {
        Progress,
        Item,
        End,
        NeedMoreData,
    }

    /// <summary>
    /// The next item, which the caller disposes; <c>null</c> after the last one.
    /// </summary>
    /// <exception cref="JsonException">The input is not a well-formed JSON array.</exception>
    public JsonDocument? ReadNext()
    {
        while (!_finished)
        {
            switch (TryReadNext(out var item))
            {
                case Step.Item:
                    return item;
                case Step.End:
                    _finished = true;
                    break;
                case Step.NeedMoreData when _endOfStream:
                    throw new JsonException("The JSON array is not closed.");
                case Step.NeedMoreData:
                    Fill();
                    break;
            }
        }

        return null;
    }

    private Step TryReadNext(out JsonDocument? item)
    {
        item = null;
        // The reader state carries the nesting and line number across buffers.
        var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), _endOfStream, _state);
        if (!reader.Read())
        {
            return Step.NeedMoreData;
        }

        if (!_inArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("The file does not contain a JSON array.");
            }

            _inArray = true;
            Commit(ref reader);
            return Step.Progress;
        }

        if (reader.TokenType == JsonTokenType.EndArray)
        {
            Commit(ref reader);
            return Step.End;
        }

        // On incomplete data this returns false and leaves the reader where it was.
        if (!JsonDocument.TryParseValue(ref reader, out item))
        {
            return Step.NeedMoreData;
        }

        Commit(ref reader);
        return Step.Item;
    }

    private void Commit(ref Utf8JsonReader reader)
    {
        _start += (int)reader.BytesConsumed;
        _state = reader.CurrentState;
    }

    private void Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        // An item larger than the buffer needs a bigger one.
        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            _endOfStream = true;
        }

        _end += read;
    }
}
