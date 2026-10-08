using System.Text;
using System.Text.Json;
using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

public sealed class JsonArrayParserTests
{
    [Fact]
    public void Reads_items_in_order()
    {
        var items = ReadAll("""[{"a": 1}, "text", [1, 2], 3, null, {"b": "ş"}]""");

        Assert.Equal(["""{"a": 1}""", "\"text\"", "[1, 2]", "3", "null", """{"b": "ş"}"""], items);
    }

    [Fact]
    public void Accepts_comments_and_trailing_commas()
    {
        Assert.Equal(["""{"a": 1}"""], ReadAll("// export\n[ {\"a\": 1}, /* last */ ]"));
    }

    [Fact]
    public void Empty_array_has_no_items()
    {
        Assert.Empty(ReadAll("  [ ]  "));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void Items_split_across_reads_are_reassembled(int chunkSize)
    {
        var expected = Enumerable.Range(0, 300).Select(i => $$"""{"n": {{i}}, "text": "satır {{i}} şğüöçı"}""").ToList();
        var json = "[" + string.Join(",\n", expected) + "]";

        Assert.Equal(expected, ReadAll(json, chunkSize));
    }

    [Fact]
    public void Items_larger_than_the_buffer_grow_it()
    {
        var big = new string('x', 300_000);
        var json = $$"""[{"big": "{{big}}"}, 42]""";

        var items = ReadAll(json, chunkSize: 10_000);

        Assert.Equal(2, items.Count);
        Assert.Equal(big, JsonDocument.Parse(items[0]).RootElement.GetProperty("big").GetString());
        Assert.Equal("42", items[1]);
    }

    [Fact]
    public void Number_at_a_read_boundary_is_not_cut()
    {
        // "12345" arrives as "123" then "45"; reading "123" as complete would be wrong.
        Assert.Equal(["12345"], ReadAll("[12345]", chunkSize: 4));
    }

    [Fact]
    public void Syntax_error_reports_the_line()
    {
        var error = Assert.ThrowsAny<JsonException>(() => ReadAll("[\n{\"a\": 1},\n{\"b\": oops}\n]", chunkSize: 3));

        Assert.Equal(2, error.LineNumber);
    }

    [Theory]
    [InlineData("[{\"a\": 1}")]
    [InlineData("{\"a\": 1}")]
    [InlineData("")]
    public void Rejects_input_that_is_not_a_closed_array(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ReadAll(json));
    }

    private static List<string> ReadAll(string json, int chunkSize = int.MaxValue)
    {
        var parser = new JsonArrayParser(new ChunkedStream(Encoding.UTF8.GetBytes(json), chunkSize));
        var items = new List<string>();
        while (parser.ReadNext() is { } document)
        {
            using (document)
            {
                items.Add(document.RootElement.GetRawText());
            }
        }

        return items;
    }

    /// <summary>
    /// Returns at most <c>chunkSize</c> bytes per read, like a slow disk or network share.
    /// </summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunkSize));
    }
}
