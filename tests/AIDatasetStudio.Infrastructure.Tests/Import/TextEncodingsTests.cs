using System.Text;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Tests.Import;

public sealed class TextEncodingsTests
{
    private const string Turkish = "Soru;Cevap\nŞifremi unuttum;Giriş ekranında 'Şifremi unuttum'a tıklayın.\nİzin günü;Yıllık izin\n";

    [Fact]
    public void Byte_order_marks_win()
    {
        Assert.Equal(CsvOptions.Utf8, TextEncodings.Detect([0xEF, 0xBB, 0xBF, (byte)'a'], isWholeFile: true));
        Assert.Equal(CsvOptions.Utf16, TextEncodings.Detect(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a")).ToArray(), isWholeFile: true));
        Assert.Equal(CsvOptions.Utf16BigEndian, TextEncodings.Detect(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("a")).ToArray(), isWholeFile: true));
    }

    [Fact]
    public void Valid_utf8_without_a_bom_is_utf8()
    {
        Assert.Equal(CsvOptions.Utf8, TextEncodings.Detect(Encoding.UTF8.GetBytes(Turkish), isWholeFile: true));
    }

    [Fact]
    public void Turkish_ansi_falls_back_to_windows_1254()
    {
        var bytes = TextEncodings.Get(CsvOptions.Windows1254).GetBytes(Turkish);

        Assert.Equal(CsvOptions.Windows1254, TextEncodings.Detect(bytes, isWholeFile: true));
        Assert.Equal(Turkish, TextEncodings.Get(CsvOptions.Windows1254).GetString(bytes));
    }

    [Fact]
    public void Character_cut_off_at_the_end_of_a_sample_is_still_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("abc ş");
        var cut = bytes[..^1];

        Assert.Equal(CsvOptions.Utf8, TextEncodings.Detect(cut, isWholeFile: false));
        Assert.Equal(CsvOptions.Windows1254, TextEncodings.Detect(cut, isWholeFile: true));
    }

    [Fact]
    public void Every_offered_encoding_is_available()
    {
        Assert.All(CsvOptions.Encodings, name => Assert.NotNull(TextEncodings.Get(name)));
    }
}
