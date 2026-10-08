using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace AIDatasetStudio.Infrastructure.Tests.Support;

/// <summary>
/// Writes small .xlsx workbooks the way Excel saves them: text in a shared string table, a
/// styles part with a date format, and cells with references. Cell values are strings, numbers,
/// <see cref="DateTime"/>s (style 1, a custom date format), booleans, <see cref="Inline"/> strings and <see cref="Formula"/>
/// results; <c>null</c> leaves a cell out and a <c>null</c> row leaves a row out.
/// </summary>
internal static class XlsxFile
{
    public static string Write(string path, params Sheet[] sheets)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Shared(string text)
        {
            if (!index.TryGetValue(text, out var i))
            {
                i = index[text] = strings.Count;
                strings.Add(text);
            }

            return i;
        }

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Part(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/></Types>
            """);
        Part(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);

        var workbook = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><workbookPr/><bookViews><workbookView activeTab="0"/></bookViews><sheets>""");
        var relationships = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var s = 0; s < sheets.Length; s++)
        {
            var sheet = sheets[s];
            var state = sheet.Hidden ? " state=\"hidden\"" : string.Empty;
            workbook.Append(CultureInfo.InvariantCulture, $"""<sheet name="{Escape(sheet.Name)}" sheetId="{s + 1}"{state} r:id="rId{s + 1}"/>""");
            relationships.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{s + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{s + 1}.xml"/>""");

            var xml = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            for (var r = 0; r < sheet.Rows.Length; r++)
            {
                if (sheet.Rows[r] is not { } row)
                {
                    continue;
                }

                xml.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 1}">""");
                for (var c = 0; c < row.Length; c++)
                {
                    var reference = Column(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                    xml.Append(row[c] switch
                    {
                        null => string.Empty,
                        string text => $"""<c r="{reference}" t="s"><v>{Shared(text)}</v></c>""",
                        Inline inline => $"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{Escape(inline.Text)}</t></is></c>""",
                        Formula formula => $"""<c r="{reference}" t="str"><f>{Escape(formula.Expression)}</f><v>{Escape(formula.Result)}</v></c>""",
                        bool flag => $"""<c r="{reference}" t="b"><v>{(flag ? 1 : 0)}</v></c>""",
                        DateTime date => string.Create(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="1"><v>{date.ToOADate()}</v></c>"""),
                        IFormattable number => $"""<c r="{reference}"><v>{number.ToString(null, CultureInfo.InvariantCulture)}</v></c>""",
                        var other => throw new ArgumentException($"Unsupported cell value {other}."),
                    });
                }

                xml.Append("</row>");
            }

            Part(zip, $"xl/worksheets/sheet{s + 1}.xml", xml.Append("</sheetData></worksheet>").ToString());
        }

        var next = sheets.Length + 1;
        relationships.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{next}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>""");
        relationships.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{next + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        Part(zip, "xl/workbook.xml", workbook.Append("</sheets></workbook>").ToString());
        Part(zip, "xl/_rels/workbook.xml.rels", relationships.Append("</Relationships>").ToString());
        Part(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy\ hh:mm"/></numFmts><cellStyleXfs count="1"><xf numFmtId="164"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" xfId="0"><alignment vertical="top"/></xf><xf numFmtId="164" applyNumberFormat="1" xfId="0"><alignment wrapText="1"/></xf><xf numFmtId="49" xfId="0"/></cellXfs></styleSheet>
            """);

        var shared = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        foreach (var text in strings)
        {
            shared.Append(CultureInfo.InvariantCulture, $"""<si><t xml:space="preserve">{Escape(text)}</t></si>""");
        }

        Part(zip, "xl/sharedStrings.xml", shared.Append("</sst>").ToString());
        return path;
    }

    public static string Column(int index) => index < 26 ? ((char)('A' + index)).ToString() : Column((index / 26) - 1) + (char)('A' + (index % 26));

    private static string Escape(string text) => SecurityElement.Escape(text);

    private static void Part(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    public sealed record Sheet(string Name, object?[]?[] Rows, bool Hidden = false);

    public sealed record Inline(string Text);

    public sealed record Formula(string Expression, string Result);
}
