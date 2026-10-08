using System.Text;
using System.Text.Json;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// Detects the format of a source file and reads it row by row.
/// </summary>
internal static class SourceFile
{
    private const int FileBufferSize = 64 * 1024;
    private const int SniffBytes = 64 * 1024;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// By extension where it is unambiguous, otherwise by the first character: "[" is a JSON
    /// array, "{" is JSON Lines (one object per line), anything else is CSV.
    /// </summary>
    public static ImportFormat DetectFormat(string path)
    {
        if (HasExtension(path, ".csv") || HasExtension(path, ".tsv"))
        {
            return ImportFormat.Csv;
        }

        if (HasExtension(path, ".jsonl") || HasExtension(path, ".ndjson"))
        {
            return ImportFormat.JsonLines;
        }

        using var reader = OpenText(path, Encoding.UTF8);
        int c;
        while ((c = reader.Read()) >= 0 && char.IsWhiteSpace((char)c))
        {
        }

        return c switch
        {
            '[' => ImportFormat.JsonArray,
            '{' => ImportFormat.JsonLines,
            _ when HasExtension(path, ".json") => ImportFormat.JsonLines,
            _ => ImportFormat.Csv,
        };
    }

    /// <summary>
    /// Guesses encoding and delimiter from the start of the file; assumes a header row.
    /// </summary>
    public static CsvOptions DetectCsvOptions(string path)
    {
        var sample = new byte[SniffBytes];
        int length;
        using (var stream = OpenRead(path))
        {
            length = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
        }

        var bytes = sample.AsSpan(0, length);
        var isWholeFile = length < sample.Length;
        var encodingName = TextEncodings.Detect(bytes, isWholeFile);
        var text = TextEncodings.Get(encodingName).GetString(bytes);
        var delimiter = CsvParser.DetectDelimiter(text, isWholeFile, HasExtension(path, ".tsv") ? '\t' : ',');
        return new CsvOptions(delimiter, encodingName, HasHeader: true);
    }

    public static IEnumerable<SourceItem> Read(string path, ImportFormat format, CsvOptions? csv) => format switch
    {
        ImportFormat.JsonLines => ReadJsonLines(path),
        ImportFormat.JsonArray => ReadJsonArray(path),
        ImportFormat.Csv => new CsvSource(path, csv ?? throw new ArgumentException("CSV options are required.", nameof(csv))).Read(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static IEnumerable<SourceItem> ReadJsonLines(string path)
    {
        using var reader = OpenText(path, Encoding.UTF8);
        long number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParse(line) is not { } document)
            {
                yield return SourceItem.Failed(number, ImportErrorCode.InvalidJson);
                continue;
            }

            using (document)
            {
                yield return ToItem(number, document.RootElement);
            }
        }
    }

    public static IEnumerable<SourceItem> ReadJsonArray(string path)
    {
        using var stream = OpenUtf8(path);
        var parser = new JsonArrayParser(stream);
        long number = 0;
        while (ReadNext(parser, path) is { } document)
        {
            using (document)
            {
                yield return ToItem(++number, document.RootElement);
            }
        }
    }

    public static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        // Let spreadsheet programs keep the file open while it is imported.
        Share = FileShare.ReadWrite | FileShare.Delete,
        BufferSize = FileBufferSize,
        Options = FileOptions.SequentialScan,
    });

    /// <summary>
    /// A reader that honours a byte order mark over <paramref name="encoding"/>.
    /// </summary>
    public static StreamReader OpenText(string path, Encoding encoding) =>
        new(OpenRead(path), encoding, detectEncodingFromByteOrderMarks: true, FileBufferSize);

    private static bool HasExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static SourceItem ToItem(long number, JsonElement root) => root.ValueKind == JsonValueKind.Object
        ? new SourceItem(number, new JsonSourceRow(number, root), null)
        : SourceItem.Failed(number, ImportErrorCode.NotAnObject);

    private static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonDocument? ReadNext(JsonArrayParser parser, string path)
    {
        try
        {
            return parser.ReadNext();
        }
        catch (JsonException ex)
        {
            throw new ImportFileException(ImportFileError.InvalidJson, path, $"'{path}' is not a valid JSON array: {ex.Message}", ex.LineNumber + 1, ex);
        }
    }

    // Utf8JsonReader takes UTF-8 without a byte order mark; skip one, and transcode UTF-16.
    private static Stream OpenUtf8(string path)
    {
        var stream = OpenRead(path);
        Span<byte> start = stackalloc byte[3];
        var length = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        start = start[..length];

        if (start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || start.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            stream.Position = 2;
            var encoding = start[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            return Encoding.CreateTranscodingStream(stream, encoding, Encoding.UTF8);
        }

        stream.Position = TextEncodings.Utf8BomLength(start);
        return stream;
    }
}

/// <summary>
/// Reads a CSV file. Row numbers count every record from the top of the file, header and blank
/// lines included, so they match the row numbers a spreadsheet shows.
/// </summary>
internal sealed class CsvSource(string path, CsvOptions options)
{
    /// <summary>
    /// The header, once the first record has been read.
    /// </summary>
    public CsvColumns? Header { get; private set; }

    /// <summary>
    /// The widest data row read so far.
    /// </summary>
    public int MaxFieldCount { get; private set; }

    /// <summary>
    /// Header names, or "#1"…"#N" for the widest row when the file has no header.
    /// </summary>
    public IReadOnlyList<string> ColumnNames => options.HasHeader ? Header?.Names ?? [] : CsvColumns.GeneratedNames(MaxFieldCount);

    public IEnumerable<SourceItem> Read()
    {
        using var reader = SourceFile.OpenText(path, TextEncodings.Get(options.EncodingName));
        var parser = new CsvParser(reader, options.Delimiter);
        var columns = options.HasHeader ? null : CsvColumns.Numbered;
        long number = 0;
        while (parser.ReadRecord() is { } record)
        {
            number++;
            if (CsvParser.IsBlank(record))
            {
                continue;
            }

            if (columns is null)
            {
                columns = Header = CsvColumns.FromHeader(record);
                continue;
            }

            MaxFieldCount = Math.Max(MaxFieldCount, record.Length);
            yield return new SourceItem(number, new CsvSourceRow(number, record, columns), null);
        }
    }
}
