using System.Globalization;
using System.Text.Json;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// One row read from a source file: a row to convert, or the reason it could not be read.
/// <see cref="Row"/> is only valid until the reader moves to the next item.
/// </summary>
internal readonly record struct SourceItem(long Number, ISourceRow? Row, ImportRowError? Error)
{
    public static SourceItem Failed(long number, ImportErrorCode code) => new(number, null, new ImportRowError(number, code));

    public ImportRowResult Convert(ImportPlan plan) =>
        Row is null ? new ImportRowResult(Number, null, Error) : ImportConverter.Convert(Row, plan);
}

internal sealed class JsonSourceRow(long number, JsonElement root) : ISourceRow
{
    public long Number => number;

    public JsonElement? Json => root;

    public JsonElement? GetElement(string column) => root.TryGetProperty(column, out var value) ? value : null;

    public string? GetText(string column) => ImportConverter.ExtractText(GetElement(column));
}

internal sealed class CsvSourceRow(long number, string[] fields, CsvColumns columns) : ISourceRow
{
    public long Number => number;

    public JsonElement? Json => null;

    public int FieldCount => fields.Length;

    public JsonElement? GetElement(string column) => null;

    public string? GetText(string column) => columns.IndexOf(column) is var index && index >= 0 && index < fields.Length ? fields[index] : null;
}

/// <summary>
/// Column names of a CSV file: its header, made unique, or "#1", "#2"… without one.
/// </summary>
internal sealed class CsvColumns
{
    private const string GeneratedPrefix = "#";

    private readonly Dictionary<string, int>? _index;

    private CsvColumns(Dictionary<string, int>? index) => _index = index;

    /// <summary>
    /// Columns are numbered; any "#N" resolves to the N-th field.
    /// </summary>
    public static CsvColumns Numbered { get; } = new(null);

    public static CsvColumns FromHeader(string[] header)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim();
            if (name.Length == 0)
            {
                name = GeneratedName(i);
            }

            var unique = name;
            for (var n = 2; index.ContainsKey(unique); n++)
            {
                unique = string.Create(CultureInfo.InvariantCulture, $"{name} ({n})");
            }

            index[unique] = i;
        }

        return new CsvColumns(index);
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

        return column.StartsWith(GeneratedPrefix, StringComparison.Ordinal)
            && int.TryParse(column.AsSpan(GeneratedPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
                ? number - 1
                : -1;
    }
}
