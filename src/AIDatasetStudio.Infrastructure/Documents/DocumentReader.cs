using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Documents;

/// <summary>
/// Reads PDF, Word, Markdown, plain-text and HTML documents and ZIPs of pages as blocks (SDD
/// §12.3). The last document read is kept: the import wizard reads a file several times
/// (analysis, preview, import) and reading a long PDF takes a while.
/// </summary>
internal sealed class DocumentReader : IDocumentReader
{
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

    // The PDF header may come after some bytes of junk; readers look this far for it.
    private const int PdfHeaderWindow = 1024;

    private readonly Lock _lock = new();
    private (string Path, long Length, DateTime Written, SourceDocument Document)? _last;

    public IReadOnlyList<string> SupportedExtensions { get; } = [".pdf", ".docx", ".docm", ".md", ".markdown", ".txt", ".html", ".htm", ".zip"];

    public async Task<SourceDocument> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            return await Task.Run(() => Read(filePath, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ImportFileException(ImportFileError.NotFound, filePath, $"'{filePath}' was not found.", innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFileException(ImportFileError.Unreadable, filePath, $"'{filePath}' cannot be read: {ex.Message}", innerException: ex);
        }
    }

    /// <summary>
    /// Reads the document on the calling thread.
    /// </summary>
    public SourceDocument Read(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"'{path}' was not found.", path);
        }

        var full = file.FullName;
        lock (_lock)
        {
            if (_last is { } last && last.Path == full && last.Length == file.Length && last.Written == file.LastWriteTimeUtc)
            {
                return last.Document;
            }
        }

        var format = DetectFormat(path);
        var document = format switch
        {
            DocumentFormat.Pdf => Pdf(path, cancellationToken),
            DocumentFormat.Word => new SourceDocument(path, format, DocxDocument.Read(path, cancellationToken)),
            DocumentFormat.Markdown => new SourceDocument(path, format, [.. TextDocuments.MarkdownBlocks([.. SourceFile.ReadTextLines(path)])]),
            DocumentFormat.Html => Html(path, cancellationToken),
            DocumentFormat.Archive => Archive(path, cancellationToken),
            _ => new SourceDocument(path, format, [.. TextDocuments.ParagraphBlocks(SourceFile.ReadTextLines(path))]),
        };

        lock (_lock)
        {
            _last = (full, file.Length, file.LastWriteTimeUtc, document);
        }

        return document;
    }

    /// <summary>
    /// The kind of document: PDF, Word and archives by their content, HTML by extension or
    /// content, Markdown by extension, otherwise plain text.
    /// </summary>
    public static DocumentFormat DetectFormat(string path)
    {
        Span<byte> start = stackalloc byte[PdfHeaderWindow];
        int length;
        using (var stream = SourceFile.OpenRead(path))
        {
            length = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        }

        start = start[..length];
        if (start.IndexOf(PdfSignature) >= 0)
        {
            return DocumentFormat.Pdf;
        }

        if (XlsxWorkbook.IsZip(start) && !SourceFile.IsWordExtension(path) && ArchiveDocument.IsPageArchive(path))
        {
            return DocumentFormat.Archive;
        }

        if (XlsxWorkbook.IsZip(start) || XlsxWorkbook.IsCompoundFile(start))
        {
            return DocumentFormat.Word;
        }

        if (IsHtml(path, start))
        {
            return DocumentFormat.Html;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            ? DocumentFormat.Markdown
            : DocumentFormat.PlainText;
    }

    /// <summary>
    /// Whether the file is an HTML page: by its extension, or by a doctype or &lt;html&gt; tag at its start.
    /// </summary>
    public static bool IsHtml(string path, ReadOnlySpan<byte> start)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = System.Text.Encoding.Latin1.GetString(start[TextEncodings.Utf8BomLength(start)..]).TrimStart();
        return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the file starts like a PDF.
    /// </summary>
    public static bool IsPdf(ReadOnlySpan<byte> start) => start.IndexOf(PdfSignature) >= 0;

    private static SourceDocument Html(string path, CancellationToken cancellationToken)
    {
        var (blocks, title) = HtmlDocument.Read(HtmlDocument.Decode(File.ReadAllBytes(path)), cancellationToken);

        // A page without headings is one section under its title.
        if (!blocks.Any(b => b.IsHeading) && !string.IsNullOrWhiteSpace(title) && blocks.Count > 0)
        {
            blocks = [new DocumentBlock(DocumentBlockKind.Heading, title, blocks[0].Position, 0), .. blocks];
        }

        return new SourceDocument(path, DocumentFormat.Html, blocks);
    }

    private static SourceDocument Archive(string path, CancellationToken cancellationToken)
    {
        var (blocks, pages) = ArchiveDocument.Read(path, cancellationToken);
        return new SourceDocument(path, DocumentFormat.Archive, blocks, pages);
    }

    private static SourceDocument Pdf(string path, CancellationToken cancellationToken)
    {
        var (blocks, pages) = PdfText.Read(path, cancellationToken);
        return new SourceDocument(path, DocumentFormat.Pdf, blocks, pages);
    }
}
