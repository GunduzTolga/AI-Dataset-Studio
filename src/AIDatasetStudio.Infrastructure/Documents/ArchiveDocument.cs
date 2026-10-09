using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Documents;

/// <summary>
/// Reads a ZIP of pages as one document (SDD §12.3, M17): a Notion export (Markdown or HTML, also
/// split into parts), a Confluence space export (HTML) or any folder of HTML, Markdown and text
/// files. Pages are read in the order of their paths, a page before the pages under it; each
/// starts with its title as the top heading, so passages carry the page they come from. A
/// block's position is its page's number.
/// </summary>
internal static partial class ArchiveDocument
{
    /// <summary>
    /// More pages than this are not read; an export that large is better split.
    /// </summary>
    public const int MaxPages = 5_000;

    // Against archives that unpack to far more than they weigh.
    private const long MaxPageBytes = 20L << 20;
    private const long MaxTotalBytes = 512L << 20;

    private static readonly string[] PageExtensions = [".html", ".htm", ".md", ".markdown", ".txt"];

    /// <summary>
    /// Whether a ZIP is an archive of pages rather than a Word document or a workbook (whether it
    /// holds pages with text is found when it is read).
    /// </summary>
    public static bool IsPageArchive(string path)
    {
        try
        {
            using var zip = new ZipArchive(SourceFile.OpenRead(path), ZipArchiveMode.Read);
            return zip.GetEntry("word/document.xml") is null && zip.GetEntry("xl/workbook.xml") is null && zip.GetEntry("[Content_Types].xml") is null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <exception cref="ImportFileException">The archive is damaged or encrypted, or holds no page with text.</exception>
    public static (IReadOnlyList<DocumentBlock> Blocks, int Pages) Read(string path, CancellationToken cancellationToken = default)
    {
        var pages = new List<(string Path, byte[] Content)>();
        long total = 0;
        try
        {
            using var zip = new ZipArchive(SourceFile.OpenRead(path), ZipArchiveMode.Read);
            Collect(zip, string.Empty, pages, ref total, nested: false, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            throw Invalid(path, ex);
        }

        // A folder's index page next to its pages is a table of contents (Confluence).
        var folders = pages.GroupBy(p => Folder(p.Path)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        pages.RemoveAll(p => folders.Contains(Folder(p.Path)) && IsIndex(p.Path));
        pages.Sort((a, b) => ComparePaths(a.Path, b.Path));

        var blocks = new List<DocumentBlock>();
        var number = 0;
        foreach (var (pagePath, content) in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = Page(pagePath, content, cancellationToken);
            if (page.Count == 0)
            {
                continue;
            }

            number++;
            blocks.AddRange(page.Select(block => block with { Position = number }));
            if (number == MaxPages)
            {
                break;
            }
        }

        if (number == 0)
        {
            throw new ImportFileException(ImportFileError.EmptyArchive, path, $"'{path}' holds no HTML, Markdown or text page with text.");
        }

        return (blocks, number);
    }

    /// <summary>
    /// A page's title from its file name: without the extension, Notion's 32-digit id and
    /// Confluence's page number; dashes and underscores read as spaces in names without spaces.
    /// </summary>
    public static string TitleFromName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        name = NotionIdRegex().Replace(name, string.Empty);
        name = ConfluenceIdRegex().Replace(name, string.Empty);
        if (!name.Contains(' ', StringComparison.Ordinal))
        {
            name = name.Replace('-', ' ').Replace('_', ' ');
        }

        return name.Trim();
    }

    // The pages of an archive; archives in it (Notion's export parts) are read too, one level deep.
    private static void Collect(ZipArchive zip, string prefix, List<(string Path, byte[] Content)> pages, ref long total, bool nested, CancellationToken cancellationToken)
    {
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || IsHidden(name) || entry.Length > MaxPageBytes * (IsArchive(name) ? 25 : 1))
            {
                continue;
            }

            if (IsArchive(name) && !nested)
            {
                var bytes = ReadEntry(entry, MaxTotalBytes - total);
                total += bytes.Length;
                try
                {
                    using var inner = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                    Collect(inner, prefix + name[..^4] + "/", pages, ref total, nested: true, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    // A damaged or encrypted archive among the pages (an attachment) is left out;
                    // the export's own pages are still read.
                }

                continue;
            }

            if (!IsPage(name) || pages.Count >= MaxPages * 2)
            {
                continue;
            }

            var content = ReadEntry(entry, Math.Min(MaxPageBytes, MaxTotalBytes - total));
            total += content.Length;
            pages.Add((prefix + name, content));
        }
    }

    // The entry's bytes, refusing more than the limit whatever its header claims.
    private static byte[] ReadEntry(ZipArchiveEntry entry, long limit)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidDataException($"'{entry.FullName}' unpacks to more than the archive may hold.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // A page's blocks, with its title as the top heading.
    private static List<DocumentBlock> Page(string path, byte[] content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        List<DocumentBlock> blocks;
        string? title = null;
        if (extension is ".html" or ".htm")
        {
            var (html, htmlTitle) = HtmlDocument.Read(HtmlDocument.Decode(content), cancellationToken);
            blocks = [.. html];
            title = htmlTitle;
        }
        else
        {
            var text = TextEncodings.Get(TextEncodings.Detect(content, isWholeFile: true)).GetString(content).TrimStart('﻿');
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            blocks = extension == ".txt" ? [.. TextDocuments.ParagraphBlocks(lines)] : [.. TextDocuments.MarkdownBlocks(lines)];
        }

        if (blocks.All(b => b.IsHeading))
        {
            return [];
        }

        // The first heading names the page when it comes first; otherwise the file name (or title) does.
        if (blocks[0].IsHeading)
        {
            blocks[0] = blocks[0] with { Level = 0 };
        }
        else
        {
            // Confluence titles read "Space : Page".
            title = title?.IndexOf(" : ", StringComparison.Ordinal) is > 0 and var colon ? title[(colon + 3)..].Trim() : title;
            var name = string.IsNullOrWhiteSpace(title) ? TitleFromName(path) : title;
            blocks.Insert(0, new DocumentBlock(DocumentBlockKind.Heading, name.Length > 0 ? name : Path.GetFileName(path), 1, 0));
        }

        return blocks;
    }

    private static bool IsPage(string name) =>
        PageExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsArchive(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    // macOS resource forks and hidden files.
    private static bool IsHidden(string name) =>
        name.StartsWith("__MACOSX/", StringComparison.Ordinal) || name.Split('/').Any(part => part.StartsWith('.'));

    private static bool IsIndex(string name) => Path.GetFileNameWithoutExtension(name).Equals("index", StringComparison.OrdinalIgnoreCase);

    private static string Folder(string name) => name.LastIndexOf('/') is var slash and >= 0 ? name[..slash] : string.Empty;

    // Path by path segment, extensions left out, so "A.md" comes before "A/B.md"; numbers in names compare as numbers.
    private static int ComparePaths(string a, string b)
    {
        var left = Path.ChangeExtension(a, null).Split('/');
        var right = Path.ChangeExtension(b, null).Split('/');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var compared = CompareNatural(left[i], right[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CompareNatural(string a, string b)
    {
        var (i, j) = (0, 0);
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (startA, startB) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                var numberA = a[startA..i].TrimStart('0');
                var numberB = b[startB..j].TrimStart('0');
                var compared = numberA.Length != numberB.Length
                    ? numberA.Length.CompareTo(numberB.Length)
                    : string.CompareOrdinal(numberA, numberB);
                if (compared != 0)
                {
                    return compared;
                }

                continue;
            }

            // Letters as people sort them: "İade" before "Kargo".
            var letters = CultureInfo.InvariantCulture.CompareInfo.Compare(a, i, 1, b, j, 1, CompareOptions.IgnoreCase);
            if (letters != 0)
            {
                return letters;
            }

            (i, j) = (i + 1, j + 1);
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static ImportFileException Invalid(string path, Exception innerException) =>
        new(ImportFileError.InvalidDocument, path, $"'{path}' is not a readable ZIP archive (damaged or encrypted).", innerException: innerException);

    [GeneratedRegex(@"\s+[0-9a-f]{32}$", RegexOptions.IgnoreCase)]
    private static partial Regex NotionIdRegex();

    [GeneratedRegex(@"_\d{4,}$")]
    private static partial Regex ConfluenceIdRegex();
}
