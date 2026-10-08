using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

public sealed class CsvParserTests
{
    [Fact]
    public void Reads_rfc4180_quoting()
    {
        var records = Parse("a,\"b, with comma\",\"say \"\"hi\"\"\"\n\"line\nbreak\",,\"x\"");

        Assert.Equal(["a", "b, with comma", "say \"hi\""], records[0]);
        Assert.Equal(["line\nbreak", "", "x"], records[1]);
    }

    [Theory]
    [InlineData("a,b\r\nc,d\r\n")]
    [InlineData("a,b\nc,d\n")]
    [InlineData("a,b\rc,d")]
    public void Accepts_every_line_ending(string text)
    {
        Assert.Equal([["a", "b"], ["c", "d"]], Parse(text));
    }

    [Fact]
    public void Keeps_quoted_crlf_inside_a_field()
    {
        Assert.Equal([["x\r\ny", "z"]], Parse("\"x\r\ny\",z\r\n"));
    }

    [Fact]
    public void Blank_lines_are_records_with_one_empty_field()
    {
        var records = Parse("a\n\nb");

        Assert.Equal([["a"], [""], ["b"]], records);
        Assert.True(CsvParser.IsBlank(records[1]));
        Assert.True(CsvParser.IsBlank([" ", "", "\t"]));
        Assert.False(CsvParser.IsBlank(["", "x"]));
    }

    [Fact]
    public void Trailing_delimiter_adds_an_empty_field()
    {
        Assert.Equal([["a", ""]], Parse("a,"));
    }

    [Fact]
    public void Malformed_quotes_are_read_leniently()
    {
        Assert.Equal([["ab\"c\"d", "e"]], Parse("ab\"c\"d,e"));
        Assert.Equal([["quoted tail", "next"]], Parse("\"quoted\" tail,next"));
        Assert.Equal([["never closed,\nto the end"]], Parse("\"never closed,\nto the end"));
    }

    [Fact]
    public void Reads_fields_longer_than_the_buffer()
    {
        var big = new string('ş', 50_000);

        Assert.Equal([[big, "x"]], Parse($"\"{big}\",x"));
    }

    [Theory]
    [InlineData("instruction,output\nMerhaba,Selam\nNasılsın,İyiyim\n", ',')]
    [InlineData("Soru;Cevap\n\"Merhaba, nasılsın?\";\"İyiyim, sağ ol.\"\nNe yapıyorsun;Kod yazıyorum, sen?\n", ';')]
    [InlineData("q\ta\nbir, iki\tüç; dört\n", '\t')]
    [InlineData("q|a\nx|y\n", '|')]
    public void Detects_the_delimiter(string sample, char expected)
    {
        Assert.Equal(expected, CsvParser.DetectDelimiter(sample, isWholeFile: true, fallback: ','));
    }

    [Fact]
    public void Falls_back_when_nothing_splits_the_header()
    {
        Assert.Equal('\t', CsvParser.DetectDelimiter("just one column\nvalue\n", isWholeFile: true, fallback: '\t'));
    }

    [Fact]
    public void Prefers_the_delimiter_with_consistent_field_counts()
    {
        // Commas appear in the text, but only semicolons split every row the same way.
        var sample = "a;b;c\nx, y;1;2\nz;3, 4, 5;6\n";

        Assert.Equal(';', CsvParser.DetectDelimiter(sample, isWholeFile: true, fallback: ','));
    }

    [Fact]
    public void Ignores_a_cut_off_last_record_in_a_partial_sample()
    {
        // The sample ends inside a quoted field, which would otherwise swallow the rest.
        var sample = "a;b\n1;2\n3;\"unterminated, with, commas";

        Assert.Equal(';', CsvParser.DetectDelimiter(sample, isWholeFile: false, fallback: ','));
    }

    private static List<string[]> Parse(string text)
    {
        var parser = new CsvParser(new StringReader(text), ',');
        var records = new List<string[]>();
        while (parser.ReadRecord() is { } record)
        {
            records.Add(record);
        }

        return records;
    }
}
