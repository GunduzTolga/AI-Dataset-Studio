using System.Text;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// Streaming RFC 4180 reader: quoted fields may contain delimiters, line breaks and doubled
/// quotes; records end with CRLF, LF or CR. Malformed input is read leniently rather than
/// rejected — an unterminated quote runs to the end of the file, and text after a closing
/// quote is kept.
/// </summary>
internal sealed class CsvParser(TextReader reader, char delimiter)
{
    private const int BufferSize = 16 * 1024;

    private readonly char[] _buffer = new char[BufferSize];
    private readonly StringBuilder _field = new();
    private readonly List<string> _fields = [];
    private int _position;
    private int _length;

    /// <summary>
    /// Reads the next record; a blank line is a record with one empty field. Returns
    /// <c>null</c> at the end of the input.
    /// </summary>
    public string[]? ReadRecord()
    {
        var c = Read();
        if (c < 0)
        {
            return null;
        }

        _fields.Clear();
        while (true)
        {
            _field.Clear();
            if (c == '"')
            {
                while ((c = Read()) >= 0)
                {
                    if (c == '"')
                    {
                        if (Peek() != '"')
                        {
                            c = Read();
                            break;
                        }

                        Read();
                    }

                    _field.Append((char)c);
                }
            }

            // Unquoted text, or stray text after a closing quote, runs to the next delimiter or line break.
            while (c >= 0 && c != delimiter && c != '\r' && c != '\n')
            {
                _field.Append((char)c);
                c = Read();
            }

            _fields.Add(_field.ToString());
            if (c != delimiter)
            {
                if (c == '\r' && Peek() == '\n')
                {
                    Read();
                }

                return [.. _fields];
            }

            c = Read();
        }
    }

    /// <summary>
    /// Whether every field of a record is empty or whitespace, as for blank lines and the
    /// ";;;" rows spreadsheets leave below the data.
    /// </summary>
    public static bool IsBlank(string[] record) => record.All(string.IsNullOrWhiteSpace);

    /// <summary>
    /// Picks the delimiter that splits the sample into the most consistent number of fields
    /// (at least two), preferring more fields; <paramref name="fallback"/> if none does.
    /// </summary>
    /// <param name="sample">The start of the file as text.</param>
    /// <param name="isWholeFile">Whether <paramref name="sample"/> is the entire file; if not, its last record may be cut off and is ignored.</param>
    /// <param name="fallback">The delimiter to use when no candidate splits the header.</param>
    public static char DetectDelimiter(string sample, bool isWholeFile, char fallback)
    {
        const int MaxRecords = 50;

        var best = fallback;
        var bestScore = (Consistency: 0.0, Fields: 0);
        foreach (var candidate in Application.Import.CsvOptions.Delimiters.OrderBy(d => d != fallback))
        {
            var parser = new CsvParser(new StringReader(sample), candidate);
            var counts = new List<int>();
            while (counts.Count <= MaxRecords && parser.ReadRecord() is { } record)
            {
                if (!IsBlank(record))
                {
                    counts.Add(record.Length);
                }
            }

            if (!isWholeFile && counts.Count > 1)
            {
                counts.RemoveAt(counts.Count - 1);
            }

            if (counts.Count == 0 || counts[0] < 2)
            {
                continue;
            }

            var score = (Consistency: counts.Count(c => c == counts[0]) / (double)counts.Count, Fields: counts[0]);
            if (score.Consistency > bestScore.Consistency || (score.Consistency == bestScore.Consistency && score.Fields > bestScore.Fields))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private int Read()
    {
        if (_position == _length && !Fill())
        {
            return -1;
        }

        return _buffer[_position++];
    }

    private int Peek() => _position == _length && !Fill() ? -1 : _buffer[_position];

    private bool Fill()
    {
        _length = reader.Read(_buffer, 0, _buffer.Length);
        _position = 0;
        return _length > 0;
    }
}
