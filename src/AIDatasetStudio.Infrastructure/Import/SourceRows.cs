using System.Globalization;
using System.Text.Json;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// One row read from a source file: a row to convert, or the reason it could not be read.
/// <see cref="Row"/> is only valid until the reader moves to the next item.
/// </summary>
/// <param name="Sheet">The workbook sheet the row is on; <c>null</c> for other formats.</param>
internal readonly record struct SourceItem(long Number, ISourceRow? Row, ImportRowError? Error, string? Sheet = null)
{
    public static SourceItem Failed(long number, ImportErrorCode code) => new(number, null, new ImportRowError(number, code));

    public ImportRowResult Convert(ImportPlan plan)
    {
        var result = Row is null ? new ImportRowResult(Number, null, Error) : ImportConverter.Convert(Row, plan);
        return Sheet is null ? result : result with { Sheet = Sheet, Error = result.Error is { } error ? error with { Sheet = Sheet } : null };
    }
}

internal sealed class JsonSourceRow(long number, JsonElement root) : ISourceRow
{
    public long Number => number;

    public JsonElement? Json => root;

    public JsonElement? GetElement(string column) => root.TryGetProperty(column, out var value) ? value : null;

    public string? GetText(string column) => ImportConverter.ExtractText(GetElement(column));
}

/// <summary>
/// A row of positional fields (CSV, Excel) read through its file's column names.
/// </summary>
internal sealed class FieldSourceRow(long number, string?[] fields, SourceColumns columns) : ISourceRow
{
    public long Number => number;

    public JsonElement? Json => null;

    public int FieldCount => fields.Length;

    public JsonElement? GetElement(string column) => null;

    public string? GetText(string column) => columns.IndexOf(column) is var index && index >= 0 && index < fields.Length ? fields[index] ?? string.Empty : null;
}

/// <summary>
/// A section or paragraph of a text document.
/// </summary>
internal sealed class TextSourceRow(TextRow row) : ISourceRow
{
    public long Number => row.Line;

    public JsonElement? Json => null;

    public JsonElement? GetElement(string column) => null;

    public string? GetText(string column) => row.Values.GetValueOrDefault(column);
}

/// <summary>
/// A conversation of a log; its messages are converted as a whole (<see cref="SourceLayout.Transcript"/>).
/// </summary>
internal sealed class TranscriptSourceRow(TranscriptReader.Conversation conversation) : ITranscriptRow
{
    public long Number => conversation.Line;

    public JsonElement? Json => null;

    public IReadOnlyList<TranscriptMessage> Messages => conversation.Messages;

    public JsonElement? GetElement(string column) => null;

    public string? GetText(string column) => null;
}

/// <summary>
/// Column names of a CSV file or a sheet: its header, made unique, or generated names without
/// one — "#1", "#2"… for CSV and the column letters for Excel.
/// </summary>
internal sealed class SourceColumns
{
    private const string GeneratedPrefix = "#";

    private readonly Dictionary<string, int>? _index;
    private readonly bool _lettered;

    private SourceColumns(Dictionary<string, int>? index, bool lettered = false)
    {
        _index = index;
        _lettered = lettered;
    }

    /// <summary>
    /// Columns are numbered; any "#N" resolves to the N-th field.
    /// </summary>
    public static SourceColumns Numbered { get; } = new(null);

    /// <summary>
    /// Columns are named by letter as in Excel; "A" resolves to the first field.
    /// </summary>
    public static SourceColumns Lettered { get; } = new(null, lettered: true);

    /// <param name="header">The first row; empty cells are named by <paramref name="generatedName"/>.</param>
    /// <param name="generatedName">Names a column by its index; "#N" when not given.</param>
    public static SourceColumns FromHeader(IReadOnlyList<string?> header, Func<int, string>? generatedName = null)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i]?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                name = (generatedName ?? GeneratedName)(i);
            }

            var unique = name;
            for (var n = 2; index.ContainsKey(unique); n++)
            {
                unique = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            }

            index[unique] = i;
        }

        return new SourceColumns(index);
    }

    public static string GeneratedName(int index) => GeneratedPrefix + (index + 1).ToString(CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> GeneratedNames(int count) => Enumerable.Range(0, count).Select(GeneratedName).ToList();

    /// <summary>
    /// Header names in file order; empty for numbered columns.
    /// </summary>
    public IReadOnlyList<string> Names => _index?.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList() ?? [];

    public int IndexOf(string column)
    {
        if (_index is not null)
        {
            return _index.TryGetValue(column, out var index) ? index : -1;
        }

        if (_lettered)
        {
            return column.Length is > 0 and <= 3 && column.All(char.IsAsciiLetterUpper) ? XlsxWorkbook.ColumnIndex(column) ?? -1 : -1;
        }

        return column.StartsWith(GeneratedPrefix, StringComparison.Ordinal)
            && int.TryParse(column.AsSpan(GeneratedPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
                ? number - 1
                : -1;
    }
}
