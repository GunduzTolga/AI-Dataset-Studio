using System.Text;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// Resolves encodings by web name and guesses the encoding of text files.
/// </summary>
internal static class TextEncodings
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16Bom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianBom = [0xFE, 0xFF];

    // windows-1254 and iso-8859-9 are not built into .NET; the code pages provider adds them.
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Get(string webName) => Encoding.GetEncoding(webName);

    /// <summary>
    /// Byte order mark first, then UTF-8 if the bytes are valid UTF-8, otherwise windows-1254 —
    /// what Excel on Turkish Windows writes when saving "CSV (comma delimited)".
    /// </summary>
    /// <param name="sample">The start of the file.</param>
    /// <param name="isWholeFile">Whether <paramref name="sample"/> is the entire file; if not, a character cut off at the end is not an error.</param>
    public static string Detect(ReadOnlySpan<byte> sample, bool isWholeFile)
    {
        if (sample.StartsWith(Utf8Bom))
        {
            return CsvOptions.Utf8;
        }

        if (sample.StartsWith(Utf16Bom))
        {
            return CsvOptions.Utf16;
        }

        if (sample.StartsWith(Utf16BigEndianBom))
        {
            return CsvOptions.Utf16BigEndian;
        }

        return IsUtf8(sample, isWholeFile) ? CsvOptions.Utf8 : CsvOptions.Windows1254;
    }

    /// <summary>
    /// The length of a UTF-8 byte order mark at the start of <paramref name="bytes"/>, or 0.
    /// </summary>
    public static int Utf8BomLength(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;

    private static bool IsUtf8(ReadOnlySpan<byte> sample, bool isWholeFile)
    {
        if (System.Text.Unicode.Utf8.IsValid(sample))
        {
            return true;
        }

        // A UTF-8 sequence is at most 4 bytes, so a cut-off one leaves up to 3 dangling bytes.
        for (var cut = 1; !isWholeFile && cut <= 3 && cut < sample.Length; cut++)
        {
            if (System.Text.Unicode.Utf8.IsValid(sample[..^cut]))
            {
                return true;
            }
        }

        return false;
    }
}
