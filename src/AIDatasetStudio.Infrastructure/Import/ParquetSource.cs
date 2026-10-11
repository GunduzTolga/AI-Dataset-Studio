using System.Buffers;
using System.Globalization;
using System.Text.Json;
using AIDatasetStudio.Application.Import;
using Parquet;
using Parquet.Serialization;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// Apache Parquet files, the form most Hugging Face datasets are published in (M25). Each row is
/// read as the JSON object it describes, so the JSON layouts (Alpaca, ShareGPT, messages, DPO)
/// are recognised as for JSON Lines; nested columns become arrays and objects. One row group is
/// held in memory at a time, and files whose row groups are too large for that are refused.
/// </summary>
internal static class ParquetSource
{
    /// <summary>
    /// The largest row group read, uncompressed. Its rows are held as objects, several times the
    /// size; Hugging Face writes groups far below this.
    /// </summary>
    internal static long MaxRowGroupBytes { get; set; } = 512L * 1024 * 1024;

    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    public static bool IsParquet(ReadOnlySpan<byte> start) => start.StartsWith(Magic);

    /// <exception cref="ImportFileException">The file is not a Parquet file that can be read.</exception>
    public static IEnumerable<SourceItem> Read(string path)
    {
        var groups = RowGroups(path);
        if (groups.FirstOrDefault(g => g.Bytes > MaxRowGroupBytes) is { Bytes: > 0 } large)
        {
            throw new ImportFileException(
                ImportFileError.ParquetTooLarge, path, string.Create(CultureInfo.InvariantCulture, $"'{path}' has a row group of {large.Bytes} bytes; at most {MaxRowGroupBytes} are read at once."));
        }

        long number = 0;
        for (var group = 0; group < groups.Count; group++)
        {
            var index = group;
            var rows = Run(path, async stream => (await ParquetSerializer.DeserializeUntypedAsync(stream, rowGroupIndex: index)).Data);
            foreach (var row in rows)
            {
                using var document = ToJson(row);
                yield return SourceFile.ToItem(++number, document.RootElement);
            }
        }
    }

    /// <summary>
    /// The number of rows, from the file's footer: nothing is decoded.
    /// </summary>
    /// <exception cref="ImportFileException">The file is not a Parquet file that can be read.</exception>
    public static long CountRows(string path) => RowGroups(path).Sum(g => g.Rows);

    private static IReadOnlyList<(long Rows, long Bytes)> RowGroups(string path) => Run(path, async stream =>
    {
        await using var reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true);
        return (IReadOnlyList<(long Rows, long Bytes)>)[.. reader.Metadata!.RowGroups.Select(g => (g.NumRows, g.TotalByteSize))];
    });

    // Parquet's reader is asynchronous; imports already run off the UI thread.
    private static T Run<T>(string path, Func<Stream, Task<T>> read)
    {
        using var stream = SourceFile.OpenRead(path);
        try
        {
            return Task.Run(() => read(stream)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ImportFileException or OutOfMemoryException))
        {
            throw new ImportFileException(ImportFileError.InvalidParquet, path, $"'{path}' is not a readable Parquet file: {ex.Message}", innerException: ex);
        }
    }

    private static JsonDocument ToJson(Dictionary<string, object> row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, row);
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case ReadOnlyMemory<char> text:
                writer.WriteStringValue(text.Span);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                WriteFloat(writer, number);
                break;
            case double number:
                WriteFloat(writer, number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTime time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan span:
                writer.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Guid id:
                writer.WriteStringValue(id);
                break;
            case byte[] bytes:
                WriteBytes(writer, bytes);
                break;
            case ReadOnlyMemory<byte> bytes:
                WriteBytes(writer, bytes.ToArray());
                break;
            case IDictionary<string, object> fields:
                writer.WriteStartObject();
                foreach (var (name, field) in fields)
                {
                    writer.WritePropertyName(name);
                    Write(writer, field);
                }

                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    // JSON has no NaN or infinity.
    private static void WriteFloat(Utf8JsonWriter writer, double number)
    {
        if (double.IsFinite(number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    // Text stored without a string annotation is still text; anything else (an image) stays out.
    private static void WriteBytes(Utf8JsonWriter writer, byte[] bytes)
    {
        try
        {
            writer.WriteStringValue(new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (System.Text.DecoderFallbackException)
        {
            writer.WriteNullValue();
        }
    }
}
