using System.Globalization;
using System.Text;

namespace AIDatasetStudio.Infrastructure.Tests.Support;

/// <summary>
/// Writes small PDFs with text placed exactly: each <see cref="Text"/> is drawn at its position in
/// Helvetica or Helvetica-Bold (the standard fonts every reader knows), on A4 pages. Turkish
/// letters that Windows-1252 lacks (ğ, Ğ, ş, Ş, ı, İ) are mapped to free codes by a
/// <c>/Differences</c> encoding, as PDF writers do.
/// </summary>
internal static class PdfFile
{
    public const double Width = 595;
    public const double Height = 842;

    private static readonly Dictionary<char, (byte Code, string Glyph)> Turkish = new()
    {
        ['ğ'] = (0x81, "gbreve"),
        ['Ğ'] = (0x8D, "Gbreve"),
        ['ş'] = (0x8F, "scedilla"),
        ['Ş'] = (0x90, "Scedilla"),
        ['ı'] = (0x9D, "dotlessi"),
        ['İ'] = (0x7F, "Idotaccent"),
    };

    // Windows-1252 codes above 0x7F for the punctuation the tests use.
    private static readonly Dictionary<char, byte> Punctuation = new()
    {
        ['•'] = 0x95,
        ['–'] = 0x96,
        ['—'] = 0x97,
        ['…'] = 0x85,
        ['’'] = 0x92,
        ['“'] = 0x93,
        ['”'] = 0x94,
    };

    /// <summary>
    /// A piece of text with its baseline at (<paramref name="X"/>, <paramref name="Y"/>), measured from the bottom left.
    /// </summary>
    public sealed record Text(double X, double Y, string Value, double Size = 10, bool Bold = false);

    /// <summary>
    /// Lines of a paragraph, one under the other with the given leading.
    /// </summary>
    public static IEnumerable<Text> Lines(double x, double y, double size, params string[] lines) =>
        lines.Select((line, i) => new Text(x, y - (i * size * 1.3), line, size));

    public static string Write(string path, params IEnumerable<Text>[] pages) => Write(path, encrypt: false, pages);

    /// <param name="encrypt">Marks the document as protected with a password nobody knows.</param>
    public static string Write(string path, bool encrypt, params IEnumerable<Text>[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, i) => $"{6 + (2 * i)} 0 R"))}] /Count {pages.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding 5 0 R >>",
            $"<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [{string.Join(' ', Turkish.Values.Select(t => $"{t.Code} /{t.Glyph}"))}] >>",
        };

        foreach (var page in pages)
        {
            var content = new StringBuilder();
            foreach (var text in page)
            {
                content.Append(CultureInfo.InvariantCulture, $"BT /{(text.Bold ? "F2" : "F1")} {text.Size} Tf 1 0 0 1 {text.X} {text.Y} Tm ({Encode(text.Value)}) Tj ET\n");
            }

            var number = objects.Count + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Width} {Height}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {number + 1} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }

        if (encrypt)
        {
            objects.Add($"<< /Filter /Standard /V 1 /R 2 /O <{new string('A', 64)}> /U <{new string('B', 64)}> /P -4 >>");
        }

        var file = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Length);
            file.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = file.Length;
        file.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            file.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        var trailer = encrypt
            ? $"/Encrypt {objects.Count} 0 R /ID [<{new string('1', 32)}> <{new string('1', 32)}>]"
            : string.Empty;
        file.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailer}>>\nstartxref\n{xref}\n%%EOF\n");

        // Every character stands for one byte.
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(file.ToString()));
        return path;
    }

    private static string Encode(string text)
    {
        var encoded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Turkish.TryGetValue(c, out var turkish))
            {
                encoded.Append((char)turkish.Code);
            }
            else if (Punctuation.TryGetValue(c, out var code))
            {
                encoded.Append((char)code);
            }
            else if (c is '(' or ')' or '\\')
            {
                encoded.Append('\\').Append(c);
            }
            else
            {
                encoded.Append(c <= 0xFF ? c : '?');
            }
        }

        return encoded.ToString();
    }
}
