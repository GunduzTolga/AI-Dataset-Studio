using System.Globalization;

namespace AIDatasetStudio.Application.Documents;

/// <summary>
/// The kinds of documents read as text for Q&amp;A extraction (SDD §12.3, §17.5).
/// </summary>
public enum DocumentFormat
{
    Pdf,
    Word,
    Markdown,
    PlainText,

    /// <summary>An HTML page (.html, .htm).</summary>
    Html,

    /// <summary>A ZIP of pages: a Notion or Confluence export, or any folder of HTML, Markdown and text files.</summary>
    Archive,
}

public enum DocumentBlockKind
{
    Heading,
    Paragraph,
    ListItem,

    /// <summary>A table, one row per line with its cells separated by " | ".</summary>
    Table,
}

/// <summary>
/// A part of a document in reading order.
/// </summary>
/// <param name="Text">The block's text; paragraphs are one line, tables and code keep their lines.</param>
/// <param name="Position">Where the block starts, 1-based: the page of a PDF or of an archive, the paragraph of a Word document, the line of a text or HTML file.</param>
/// <param name="Level">The level of a heading (0 for a document title, then 1, 2…); the depth of a list item (0 at the top); otherwise 0.</param>
public sealed record DocumentBlock(DocumentBlockKind Kind, string Text, int Position, int Level = 0)
{
    public bool IsHeading => Kind == DocumentBlockKind.Heading;
}

/// <summary>
/// A document read as text.
/// </summary>
/// <param name="PageCount">The pages of a PDF or of an archive; 0 for other formats.</param>
public sealed record SourceDocument(string FilePath, DocumentFormat Format, IReadOnlyList<DocumentBlock> Blocks, int PageCount = 0)
{
    public string FileName => Path.GetFileName(FilePath);

    public bool HasHeadings => Blocks.Any(b => b.IsHeading);

    /// <summary>
    /// The unit of <see cref="DocumentBlock.Position"/> in lineage references: "page", "paragraph" or "line".
    /// </summary>
    public string PositionUnit => Format switch
    {
        DocumentFormat.Pdf or DocumentFormat.Archive => "page",
        DocumentFormat.Word => "paragraph",
        _ => "line",
    };

    /// <summary>
    /// A lineage reference to a place in the document, e.g. "kılavuz.pdf#page=3" (the fragment
    /// PDF viewers understand).
    /// </summary>
    public string Reference(int position) => string.Create(CultureInfo.InvariantCulture, $"{FileName}#{PositionUnit}={position}");
}

/// <summary>
/// Reads PDF, Word, Markdown, plain-text and HTML documents, and ZIPs of pages, as blocks of text.
/// </summary>
public interface IDocumentReader
{
    /// <summary>
    /// File extensions read as documents, with the leading dot.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Reads the document. The last document read is kept, so reading it again is free until the file changes.
    /// </summary>
    /// <exception cref="Import.ImportFileException">
    /// The file cannot be read: missing, unreadable, a Word 97–2003 or protected document, a
    /// damaged file, a password-protected PDF, a PDF without text (scanned pages), or a ZIP
    /// without pages.
    /// </exception>
    Task<SourceDocument> ReadAsync(string filePath, CancellationToken cancellationToken = default);
}
