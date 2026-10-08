using System.IO.Compression;
using System.Security;

namespace AIDatasetStudio.Infrastructure.Tests.Support;

/// <summary>
/// Writes .docx packages the way Word lays them out: the document, its styles and numbering
/// parts found through relationships. The body is WordprocessingML with the usual "w:" prefix;
/// <see cref="P"/> writes a simple paragraph.
/// </summary>
internal static class DocxFile
{
    public const string Namespaces =
        """xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" xmlns:v="urn:schemas-microsoft-com:vml" mc:Ignorable="wps" """;

    /// <summary>
    /// Built-in styles with the ids Turkish Word gives them ("Balk1" for heading 1); their names
    /// stay English, as in every language of Word.
    /// </summary>
    public const string DefaultStyles = """
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="KonuBal"><w:name w:val="Title"/><w:basedOn w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="Balk1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Balk2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
        <w:style w:type="paragraph" w:styleId="SoruBasligi"><w:name w:val="Soru Başlığı"/><w:basedOn w:val="Balk2"/></w:style>
        <w:style w:type="paragraph" w:styleId="ListeMaddesi"><w:name w:val="List Bullet"/><w:basedOn w:val="Normal"/><w:pPr><w:numPr><w:numId w:val="2"/></w:numPr></w:pPr></w:style>
        <w:style w:type="paragraph" w:styleId="T1"><w:name w:val="toc 1"/><w:basedOn w:val="Normal"/></w:style>
        <w:style w:type="character" w:styleId="Balk1Char"><w:name w:val="Başlık 1 Char"/><w:link w:val="Balk1"/></w:style>
        """;

    /// <summary>
    /// List 1: "1." then "a)" one level down; list 2: bullets.
    /// </summary>
    public const string DefaultNumbering = """
        <w:abstractNum w:abstractNumId="10"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl><w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%2)"/></w:lvl></w:abstractNum>
        <w:abstractNum w:abstractNumId="20"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/></w:lvl></w:abstractNum>
        <w:num w:numId="1"><w:abstractNumId w:val="10"/></w:num>
        <w:num w:numId="2"><w:abstractNumId w:val="20"/></w:num>
        <w:num w:numId="3"><w:abstractNumId w:val="10"/><w:lvlOverride w:ilvl="0"><w:startOverride w:val="5"/></w:lvlOverride></w:num>
        """;

    public static string Write(string path, string body, string styles = DefaultStyles, string numbering = DefaultNumbering)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Part(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/></Types>
            """);
        Part(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
            """);
        Part(zip, "word/_rels/document.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com" TargetMode="External"/></Relationships>
            """);
        Part(zip, "word/styles.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles {Namespaces}>{styles}</w:styles>""");
        Part(zip, "word/numbering.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:numbering {Namespaces}>{numbering}</w:numbering>""");
        Part(zip, "word/document.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document {Namespaces}><w:body>{body}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/></w:sectPr></w:body></w:document>""");
        return path;
    }

    /// <summary>
    /// A paragraph of one run, with a style when given.
    /// </summary>
    public static string P(string text, string? style = null) =>
        $"""<w:p>{(style is null ? string.Empty : $"""<w:pPr><w:pStyle w:val="{style}"/></w:pPr>""")}<w:r><w:t xml:space="preserve">{SecurityElement.Escape(text)}</w:t></w:r></w:p>""";

    private static void Part(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
