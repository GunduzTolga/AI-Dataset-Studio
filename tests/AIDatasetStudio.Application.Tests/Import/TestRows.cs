using System.Text.Json;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Application.Tests.Import;

/// <summary>
/// Minimal source rows for converter tests.
/// </summary>
internal static class TestRows
{
    public static ISourceRow Json(string json, long number = 1) => new JsonRow(number, JsonDocument.Parse(json).RootElement.Clone());

    public static ISourceRow Columns(params (string Column, string Value)[] cells) => new ColumnRow(1, cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal));

    private sealed class JsonRow(long number, JsonElement element) : ISourceRow
    {
        public long Number => number;

        public JsonElement? Json => element;

        public JsonElement? GetElement(string column) => element.TryGetProperty(column, out var value) ? value : null;

        public string? GetText(string column) => ImportConverter.ExtractText(GetElement(column));
    }

    private sealed class ColumnRow(long number, Dictionary<string, string> cells) : ISourceRow
    {
        public long Number => number;

        public JsonElement? Json => null;

        public JsonElement? GetElement(string column) => null;

        public string? GetText(string column) => cells.TryGetValue(column, out var value) ? value : null;
    }
}
