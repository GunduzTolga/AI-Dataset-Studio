using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Documents;

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
    /// The format to read the file as, and the formats the user may choose instead. By content
    /// for PDFs, Word documents and workbooks, by extension where it is unambiguous, otherwise by the first character:
    /// "[" is a JSON array, "{" is JSON Lines (one object per line), and anything else is a table
    /// (CSV) or plain text, whichever the file looks like.
    /// </summary>
    /// <exception cref="ImportFileException">The file is an Excel or Word 97–2003 or password-protected document.</exception>
    public static (ImportFormat Format, IReadOnlyList<ImportFormat> ReadableAs) DetectFormat(string path)
    {
        Span<byte> start = stackalloc byte[1024];
        int length;
        using (var stream = OpenRead(path))
        {
            length = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        }

        start = start[..length];
        if (XlsxWorkbook.IsCompoundFile(start))
        {
            throw IsWordExtension(path)
                ? DocxDocument.Legacy(path)
                : new ImportFileException(ImportFileError.LegacyWorkbook, path, $"'{path}' is an Excel 97-2003 or password-protected workbook.");
        }

        if (DocumentReader.IsPdf(start))
        {
            return (ImportFormat.Pdf, [ImportFormat.Pdf]);
        }

        if (IsWordExtension(path) || (XlsxWorkbook.IsZip(start) && IsWordPackage(path)))
        {
            return (ImportFormat.Word, [ImportFormat.Word]);
        }

        // A ZIP of pages (Notion, Confluence) rather than a workbook.
        if (XlsxWorkbook.IsZip(start) && !HasExtension(path, ".xlsx") && !HasExtension(path, ".xlsm") && ArchiveDocument.IsPageArchive(path))
        {
            return (ImportFormat.Archive, [ImportFormat.Archive]);
        }

        if (XlsxWorkbook.IsZip(start) || HasExtension(path, ".xlsx") || HasExtension(path, ".xlsm"))
        {
            return (ImportFormat.Excel, [ImportFormat.Excel]);
        }

        if (DocumentReader.IsHtml(path, start))
        {
            return (ImportFormat.Html, [ImportFormat.Html]);
        }

        if (HasExtension(path, ".csv") || HasExtension(path, ".tsv"))
        {
            return (ImportFormat.Csv, [ImportFormat.Csv]);
        }

        if (HasExtension(path, ".jsonl") || HasExtension(path, ".ndjson"))
        {
            return (ImportFormat.JsonLines, [ImportFormat.JsonLines]);
        }

        if (HasExtension(path, ".md") || HasExtension(path, ".markdown"))
        {
            return (ImportFormat.Markdown, [ImportFormat.Markdown, ImportFormat.PlainText]);
        }

        using (var reader = OpenText(path, Encoding.UTF8))
        {
            int c;
            while ((c = reader.Read()) >= 0 && char.IsWhiteSpace((char)c))
            {
            }

            if (c == '[')
            {
                return (ImportFormat.JsonArray, [ImportFormat.JsonArray]);
            }

            if (c == '{' || HasExtension(path, ".json"))
            {
                return (ImportFormat.JsonLines, [ImportFormat.JsonLines]);
            }
        }

        return (LooksLikeTable(path) ? ImportFormat.Csv : ImportFormat.PlainText, [ImportFormat.Csv, ImportFormat.PlainText]);
    }

    /// <summary>
    /// Guesses encoding and delimiter from the start of the file; assumes a header row.
    /// </summary>
    public static CsvOptions DetectCsvOptions(string path)
    {
        var (text, isWholeFile, encodingName) = ReadSample(path);
        var delimiter = CsvParser.DetectDelimiter(text, isWholeFile, HasExtension(path, ".tsv") ? '\t' : ',');
        return new CsvOptions(delimiter, encodingName, HasHeader: true);
    }

    public static IEnumerable<SourceItem> Read(string path, ImportFormat format, CsvOptions? csv, SheetOptions? sheet = null) => format switch
    {
        ImportFormat.JsonLines => ReadJsonLines(path),
        ImportFormat.JsonArray => ReadJsonArray(path),
        ImportFormat.Csv => new CsvSource(path, csv ?? throw new ArgumentException("CSV options are required.", nameof(csv))).Read(),
        ImportFormat.Excel => new ExcelSource(path, sheet ?? new SheetOptions(null, HasHeader: true)).Read(),
        ImportFormat.Markdown => TextItems(TextDocuments.Sections([.. ReadTextLines(path)])),
        ImportFormat.PlainText => TextItems(TextDocuments.Paragraphs(ReadTextLines(path))),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// The lines of a text file in its detected encoding (byte order mark, UTF-8 or windows-1254).
    /// </summary>
    public static IEnumerable<string> ReadTextLines(string path)
    {
        var encodingName = ReadSample(path).EncodingName;
        using var reader = OpenText(path, TextEncodings.Get(encodingName));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

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

    public static bool IsWordExtension(string path) =>
        HasExtension(path, ".docx") || HasExtension(path, ".docm") || HasExtension(path, ".doc") || HasExtension(path, ".dotx");

    /// <summary>
    /// Whether a ZIP file is a Word document rather than a workbook.
    /// </summary>
    private static bool IsWordPackage(string path)
    {
        try
        {
            using var zip = new ZipArchive(OpenRead(path), ZipArchiveMode.Read);
            return zip.GetEntry("word/document.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static IEnumerable<SourceItem> TextItems(IEnumerable<TextRow> rows) =>
        rows.Select(row => new SourceItem(row.Line, new TextSourceRow(row), null));

    /// <summary>
    /// The start of the file as text, whether that is the whole file, and its detected encoding.
    /// </summary>
    private static (string Text, bool IsWholeFile, string EncodingName) ReadSample(string path)
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
        return (TextEncodings.Get(encodingName).GetString(bytes), isWholeFile, encodingName);
    }

    /// <summary>
    /// A table has the same number of delimited fields on (nearly) every line and no blank lines
    /// between rows; prose has blank lines between paragraphs or no steady delimiter.
    /// </summary>
    private static bool LooksLikeTable(string path)
    {
        var (text, isWholeFile, _) = ReadSample(path);
        var lines = text.Split('\n');
        var count = isWholeFile ? lines.Length : lines.Length - 1;
        var seenText = false;
        var blankAfterText = false;
        for (var i = 0; i < count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                blankAfterText |= seenText;
            }
            else if (blankAfterText)
            {
                return false;
            }
            else
            {
                seenText = true;
            }
        }

        var (_, fields, consistency) = CsvParser.Score(text, isWholeFile, ',');
        return fields >= 2 && consistency >= 0.9;
    }

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
/// Reads one sheet or every visible sheet of a workbook. Row numbers are the sheet's own; with a
/// header, each sheet's first row with a value names its columns, and the columns of all sheets
/// are offered together.
/// </summary>
internal sealed class ExcelSource(string path, SheetOptions options)
{
    private readonly List<string> _columns = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// The widest data row read so far.
    /// </summary>
    public int MaxFieldCount { get; private set; }

    /// <summary>
    /// Header names of the sheets read, or column letters up to the widest row without a header.
    /// </summary>
    public IReadOnlyList<string> ColumnNames => options.HasHeader
        ? _columns
        : [.. Enumerable.Range(0, MaxFieldCount).Select(XlsxWorkbook.ColumnName)];

    /// <summary>
    /// The visible sheets of the workbook and the one to read first.
    /// </summary>
    public static (IReadOnlyList<string> Sheets, string? Active) ListSheets(string path)
    {
        using var workbook = XlsxWorkbook.Open(path);
        return ([.. workbook.VisibleSheets.Select(s => s.Name)], workbook.ActiveSheet?.Name);
    }

    public IEnumerable<SourceItem> Read()
    {
        using var workbook = XlsxWorkbook.Open(path);
        IReadOnlyList<XlsxSheet> sheets = options.Sheet is { } name
            ? [workbook.Find(name) ?? throw new ImportFileException(ImportFileError.SheetNotFound, path, $"'{path}' has no sheet named '{name}'.")]
            : workbook.VisibleSheets;

        foreach (var sheet in sheets)
        {
            var columns = options.HasHeader ? null : SourceColumns.Lettered;
            foreach (var (number, cells) in workbook.ReadRows(sheet))
            {
                var width = Array.FindLastIndex(cells, c => !string.IsNullOrWhiteSpace(c)) + 1;
                if (width == 0)
                {
                    continue;
                }

                if (columns is null)
                {
                    columns = SourceColumns.FromHeader(cells[..width], XlsxWorkbook.ColumnName);
                    foreach (var column in columns.Names.Where(_seen.Add))
                    {
                        _columns.Add(column);
                    }

                    continue;
                }

                MaxFieldCount = Math.Max(MaxFieldCount, width);
                yield return new SourceItem(number, new FieldSourceRow(number, cells, columns), null, sheet.Name);
            }
        }
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
    public SourceColumns? Header { get; private set; }

    /// <summary>
    /// The widest data row read so far.
    /// </summary>
    public int MaxFieldCount { get; private set; }

    /// <summary>
    /// Header names, or "#1"…"#N" for the widest row when the file has no header.
    /// </summary>
    public IReadOnlyList<string> ColumnNames => options.HasHeader ? Header?.Names ?? [] : SourceColumns.GeneratedNames(MaxFieldCount);

    public IEnumerable<SourceItem> Read()
    {
        using var reader = SourceFile.OpenText(path, TextEncodings.Get(options.EncodingName));
        var parser = new CsvParser(reader, options.Delimiter);
        var columns = options.HasHeader ? null : SourceColumns.Numbered;
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
                columns = Header = SourceColumns.FromHeader(record);
                continue;
            }

            MaxFieldCount = Math.Max(MaxFieldCount, record.Length);
            yield return new SourceItem(number, new FieldSourceRow(number, record, columns), null);
        }
    }
}
