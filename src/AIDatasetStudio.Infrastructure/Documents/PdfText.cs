using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Import;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace AIDatasetStudio.Infrastructure.Documents;

/// <summary>
/// Reads the text of a PDF as blocks (SDD §12.3). PdfPig finds the words, text blocks and their
/// reading order (columns included); this class turns their lines into headings, paragraphs,
/// list items and table rows:
/// <list type="bullet">
/// <item>Headings are lines set larger than the body text (levels by size), or short bold lines
/// at body size followed by plain text.</item>
/// <item>Running headers and footers (the same text at the top or bottom of most pages) and page
/// numbers are left out, and so are table of contents lines ("Giriş ....... 3").</item>
/// <item>List markers (bullets, "1.", "a)") join the text on their line; lines of a table row
/// are joined with " | ".</item>
/// <item>Lines are joined into paragraphs; a word hyphenated at the end of a line is joined
/// again, and a paragraph that goes on to the next page or column is kept whole.</item>
/// </list>
/// </summary>
internal static partial class PdfText
{
    // Lines this close to the top or bottom edge (as a share of the page height) can be running headers or footers.
    private const double MarginZone = 0.08;

    // A line is a heading when it is at least this much larger than the body text (and at least 1 pt).
    private const double HeadingScale = 1.12;

    private const int MaxHeadingLines = 3;
    private const int MaxHeadingLength = 200;
    private const int MaxBoldHeadingLength = 120;
    private const int MaxHeadingLevels = 6;

    // Words this many font sizes apart on one line may be table cells …
    private const double CellGap = 2.5;

    // … and are when every gap of the line is this wide, which justified text does not reach.
    private const double EvenCellGap = 4;

    // Table cells are short: a block of lines this long on average is a column of prose.
    private const int MaxCellLine = 30;

    // Bold lines are not headings in documents with this share of their body text in bold.
    private const double MaxBoldShare = 0.4;

    /// <exception cref="ImportFileException">The PDF needs a password, is damaged, or has no text.</exception>
    public static (IReadOnlyList<DocumentBlock> Blocks, int PageCount) Read(string path, CancellationToken cancellationToken)
    {
        using var stream = SourceFile.OpenRead(path);
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new ImportFileException(ImportFileError.ProtectedPdf, path, $"'{path}' is protected with a password.", innerException: ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            throw new ImportFileException(ImportFileError.InvalidDocument, path, $"'{path}' is not a valid PDF document.", innerException: ex);
        }

        using (document)
        {
            var lines = new List<Line>();
            var characters = 0;
            var pages = document.NumberOfPages;
            if (pages == 0)
            {
                throw new ImportFileException(ImportFileError.InvalidDocument, path, $"'{path}' has no pages.");
            }

            for (var number = 1; number <= pages; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    characters += ReadPage(document.GetPage(number), lines);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or IOException))
                {
                    // A page PdfPig cannot read is left out; the rest of the document still counts.
                }
            }

            if (characters == 0)
            {
                throw new ImportFileException(ImportFileError.NoText, path, $"'{path}' has no text; its pages may be scanned images.");
            }

            RemoveDecorations(lines, pages);
            return (new BlockBuilder(lines).Build(), pages);
        }
    }

    /// <summary>
    /// Adds the page's lines in reading order.
    /// </summary>
    /// <returns>The number of characters on the page.</returns>
    private static int ReadPage(Page page, List<Line> lines)
    {
        var letters = DuplicateOverlappingTextProcessor.Get(page.Letters);
        var characters = letters.Count(l => !string.IsNullOrWhiteSpace(l.Value));
        if (characters == 0)
        {
            return 0;
        }

        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters);
        var blocks = UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words));
        // PdfPig may cut a justified paragraph with wide gaps between its words into narrow
        // blocks; a block that lies within an earlier one and shares its baselines goes back in.
        var groups = new List<(List<Word> Words, List<double> Baselines, double Left, double Right)>();
        foreach (var block in blocks.OrderBy(b => b.ReadingOrder))
        {
            // Vertical text in the margin (a document id, a watermark) is not part of the reading.
            if (block.TextOrientation != TextOrientation.Horizontal)
            {
                continue;
            }

            var blockWords = block.TextLines.SelectMany(l => l.Words).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (blockWords.Count == 0)
            {
                continue;
            }

            var left = blockWords.Min(w => w.BoundingBox.Left);
            var right = blockWords.Max(w => w.BoundingBox.Right);
            var host = groups.FindIndex(g => left >= g.Left - 2 && right <= g.Right + 2
                && blockWords.All(w => g.Baselines.Any(b => Math.Abs(b - Baseline(w)) <= 0.4 * WordSize(w))));

            // The columns of a table may come as blocks side by side, line for line.
            if (host < 0 && groups.Count > 0 && IsNextColumn(groups[^1].Words, blockWords))
            {
                host = groups.Count - 1;
            }

            if (host >= 0)
            {
                var (hostWords, baselines, hostLeft, hostRight) = groups[host];
                hostWords.AddRange(blockWords);
                baselines.AddRange(blockWords.Select(Baseline));
                groups[host] = (hostWords, baselines, Math.Min(hostLeft, left), Math.Max(hostRight, right));
            }
            else
            {
                groups.Add((blockWords, [.. blockWords.Select(Baseline)], left, right));
            }
        }

        var pageLines = new List<Line>();
        for (var i = 0; i < groups.Count; i++)
        {
            pageLines.AddRange(Lines(groups[i].Words, page, i + 1));
        }

        JoinListMarkers(pageLines);
        MarkItems(pageLines);
        lines.AddRange(pageLines);
        return characters;
    }

    /// <summary>
    /// A line that is only a list marker (a bullet, "1.", "a)") is joined to the text on its
    /// baseline just to the right; PdfPig often puts the markers in a block of their own.
    /// </summary>
    private static void JoinListMarkers(List<Line> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var marker = lines[i];
            if (ListMarker().Match(marker.Text) is not { Success: true } match || match.Length != marker.Text.Length)
            {
                continue;
            }

            var item = lines
                .Where(l => l != marker && !l.StartsItem && SameBaseline(l, marker) && l.Box.Left >= marker.Box.Right - 1 && l.Box.Left - marker.Box.Right <= 4 * marker.Size)
                .MinBy(l => l.Box.Left);
            if (item is null)
            {
                continue;
            }

            item.Text = $"{Label(match.Value)} {item.Text}";
            item.TextLeft = item.Box.Left;
            item.StartsItem = true;
            lines.RemoveAt(i);
        }
    }

    /// <summary>
    /// The lines of a text block: its words grouped by baseline, top to bottom. PdfPig's own lines
    /// go wrong in narrow justified columns, where the gaps between words are wide. Words far
    /// apart on one baseline are the cells of a table row and are joined with " | ".
    /// </summary>
    private static IEnumerable<Line> Lines(List<Word> words, Page page, int index)
    {
        var rows = new List<(double Baseline, double Size, List<Word> Words)>();
        foreach (var word in words)
        {
            var baseline = Baseline(word);
            var size = WordSize(word);
            var row = rows.FindIndex(r => Math.Abs(r.Baseline - baseline) <= 0.4 * Math.Max(r.Size, size));
            if (row < 0)
            {
                rows.Add((baseline, size, [word]));
            }
            else
            {
                rows[row].Words.Add(word);
            }
        }

        foreach (var (baseline, _, rowWords) in rows.OrderByDescending(r => r.Baseline))
        {
            rowWords.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            var glyphs = rowWords.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            var size = Median(glyphs.Select(l => l.PointSize));
            var (text, isRow) = Join(rowWords, size);
            var normalized = Normalize(text);
            if (normalized.Length == 0)
            {
                continue;
            }

            var box = new PdfRectangle(
                rowWords.Min(w => w.BoundingBox.Left),
                rowWords.Min(w => w.BoundingBox.Bottom),
                rowWords.Max(w => w.BoundingBox.Right),
                rowWords.Max(w => w.BoundingBox.Top));
            yield return new Line(normalized, size, glyphs.All(IsBold), box, page.Number, index, page.Height) { IsRow = isRow, Baseline = baseline };
        }
    }

    /// <summary>
    /// Whether <paramref name="block"/> is the next column of a table whose cells so far are
    /// <paramref name="table"/>: to the right of it, on the same baselines line for line, and
    /// with short lines (columns of prose have long ones).
    /// </summary>
    private static bool IsNextColumn(List<Word> table, List<Word> block)
    {
        if (block.Min(w => w.BoundingBox.Left) < table.Max(w => w.BoundingBox.Right))
        {
            return false;
        }

        var size = WordSize(block[0]);
        var ours = LineBaselines(table, size);
        var theirs = LineBaselines(block, size);
        return theirs.Count >= 2
            && theirs.All(b => ours.Any(o => Math.Abs(o - b) <= 0.4 * size))
            && ours.All(o => theirs.Any(b => Math.Abs(o - b) <= 0.4 * size))
            && block.Sum(w => w.Text.Length + 1) <= MaxCellLine * theirs.Count;
    }

    /// <summary>
    /// The distinct baselines of the words.
    /// </summary>
    private static List<double> LineBaselines(IEnumerable<Word> words, double size)
    {
        var baselines = new List<double>();
        foreach (var baseline in words.Select(Baseline).Order())
        {
            if (baselines.Count == 0 || baseline - baselines[^1] > 0.4 * size)
            {
                baselines.Add(baseline);
            }
        }

        return baselines;
    }

    /// <summary>
    /// The words of a line as text, and whether they are the cells of a table row. Cells are
    /// words far apart; but justified text spreads its words too, evenly, so a line whose gaps
    /// are all alike is a table row only when they are very wide.
    /// </summary>
    private static (string Text, bool IsRow) Join(List<Word> words, double size)
    {
        var gaps = new double[words.Count - 1];
        for (var i = 1; i < words.Count; i++)
        {
            gaps[i - 1] = words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right;
        }

        var isRow = gaps.Length > 0 && gaps.Max() >= CellGap * size
            && (gaps.Min() < 0.6 * gaps.Max() || gaps.Max() >= EvenCellGap * size);
        var text = new StringBuilder(words[0].Text);
        for (var i = 1; i < words.Count; i++)
        {
            text.Append(isRow && gaps[i - 1] >= CellGap * size ? " | " : " ").Append(words[i].Text);
        }

        return (text.ToString(), isRow);
    }

    /// <summary>
    /// A line that begins with a list marker and its text: bullets always begin an item; numbers
    /// ("3.", "b)") only where a sentence ended before, since a wrapped line may begin with one.
    /// </summary>
    private static void MarkItems(List<Line> lines)
    {
        foreach (var line in lines.Where(l => !l.StartsItem))
        {
            if (ListMarker().Match(line.Text) is not { Success: true } match || match.Length >= line.Text.Length || line.Text[match.Length] != ' ')
            {
                continue;
            }

            var label = Label(match.Value);
            line.Text = $"{label} {line.Text[(match.Length + 1)..].TrimStart()}";
            line.TextLeft = line.Box.Left + (match.Length + 1) * line.Size * 0.5;
            if (label == "-")
            {
                line.StartsItem = true;
            }
            else
            {
                line.MayStartItem = true;
            }
        }
    }

    /// <summary>
    /// Removes running headers and footers (the same text, numbers aside, near the top or bottom
    /// of most pages), page numbers, and table of contents lines.
    /// </summary>
    private static void RemoveDecorations(List<Line> lines, int pages)
    {
        var repeated = lines
            .Where(l => l.Zone != 0)
            .GroupBy(l => (l.Zone, Key: DecorationKey(l.Text)))
            .Where(g => pages >= 3 && g.Select(l => l.Page).Distinct().Count() >= Math.Max(3, pages / 2.0))
            .Select(g => g.Key)
            .ToHashSet();

        lines.RemoveAll(l =>
            (l.Zone != 0 && (PageNumber().IsMatch(l.Text) || repeated.Contains((l.Zone, DecorationKey(l.Text)))))
            || TableOfContentsLine().IsMatch(l.Text));
    }

    private static string DecorationKey(string text) =>
        Digits().Replace(text.ToLowerInvariant(), "#");

    private static bool SameBaseline(Line a, Line b) => Math.Abs(a.Baseline - b.Baseline) <= 0.4 * Math.Max(a.Size, b.Size);

    private static double Baseline(Word word) => word.Letters.Average(l => l.StartBaseLine.Y);

    private static double WordSize(Word word) => Median(word.Letters.Select(l => l.PointSize));

    private static bool IsBold(Letter letter) =>
        letter.FontDetails?.IsBold == true
        || (letter.FontName is { } name && (name.Contains("bold", StringComparison.OrdinalIgnoreCase) || name.Contains("black", StringComparison.OrdinalIgnoreCase) || name.Contains("heavy", StringComparison.OrdinalIgnoreCase)));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return Math.Round(sorted[sorted.Count / 2], 1);
    }

    /// <summary>
    /// "•", "", "–" become "-"; "1." and "a)" stay.
    /// </summary>
    private static string Label(string marker) => marker.Length > 1 && marker[^1] is '.' or ')' ? marker : "-";

    /// <summary>
    /// Ligatures as letters, soft hyphens removed and white space collapsed.
    /// </summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            _ = c switch
            {
                'ﬀ' => builder.Append("ff"),
                'ﬁ' => builder.Append("fi"),
                'ﬂ' => builder.Append("fl"),
                'ﬃ' => builder.Append("ffi"),
                'ﬄ' => builder.Append("ffl"),
                'ﬅ' or 'ﬆ' => builder.Append("st"),
                '­' => builder,
                _ when char.IsWhiteSpace(c) => builder.Append(' '),
                _ => builder.Append(c),
            };
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // A bullet (including symbol-font bullets in the private use area), a number or a letter with "." or ")".
    [GeneratedRegex(@"^(?:[•●○◦▪■□‣⁃–—∙·*\-]|[-]|\(?\d{1,3}[.)]|\(?[a-zçğıöşü][.)])")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^(?:(?:sayfa|page|sf\.?|s\.)\s*)?[-–—]?\s*(?:\d{1,4}|[ivxlc]{1,7})\s*[-–—]?(?:\s*(?:/|of|\||-)\s*\d{1,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();

    // "Giriş ........ 3", "Giriş … 3": dot leaders before a page number.
    [GeneratedRegex(@"(?:\.\s?){4,}\s*\d{1,4}$|…{2,}\s*\d{1,4}$")]
    private static partial Regex TableOfContentsLine();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>
    /// A line of text on a page.
    /// </summary>
    /// <param name="Size">The median point size of its letters.</param>
    /// <param name="Block">The text block it is in, counted on its page in reading order.</param>
    private sealed class Line(string text, double size, bool bold, PdfRectangle box, int page, int block, double pageHeight)
    {
        public string Text { get; set; } = text;

        public double Size { get; } = size;

        public bool IsBold { get; } = bold;

        public PdfRectangle Box { get; set; } = box;

        /// <summary>
        /// The height of the line's baseline on the page (PDF coordinates grow upwards).
        /// </summary>
        public double Baseline { get; init; }

        public int Page { get; } = page;

        public int Block { get; } = block;

        /// <summary>
        /// -1 near the bottom edge, 1 near the top edge, 0 in between.
        /// </summary>
        public int Zone { get; } = box.Top >= pageHeight * (1 - MarginZone) ? 1 : box.Bottom <= pageHeight * MarginZone ? -1 : 0;

        /// <summary>
        /// The line begins a list item (its marker was joined to it).
        /// </summary>
        public bool StartsItem { get; set; }

        /// <summary>
        /// Where the item's text begins, for its following lines.
        /// </summary>
        public double TextLeft { get; set; } = box.Left;

        /// <summary>
        /// The line begins with a number or letter label ("3.", "b)") that may begin a list item.
        /// </summary>
        public bool MayStartItem { get; set; }

        /// <summary>
        /// The line is a table row of several cells.
        /// </summary>
        public bool IsRow { get; set; }

        public bool EndsSentence => Text.Length > 0 && Text[^1] is '.' or '!' or '?' or ':' or '…' or '"' or '”' or ')';

        public bool StartsLowercase => Text.Length > 0 && char.IsLower(Text[0]);
    }

    /// <summary>
    /// Turns lines into headings, paragraphs, list items and tables.
    /// </summary>
    private sealed class BlockBuilder
    {
        private readonly List<Line> _lines;
        private readonly double _body;
        private readonly Dictionary<double, int> _headingLevels;
        private readonly int _boldLevel;
        private readonly bool _boldHeadings;
        private readonly Dictionary<Line, double> _columnRight;
        private readonly List<DocumentBlock> _blocks = [];

        private readonly StringBuilder _text = new();
        private DocumentBlockKind? _kind;
        private int _position;
        private int _level;
        private int _lineCount;
        private double _itemLeft;
        private Line? _last;

        public BlockBuilder(List<Line> lines)
        {
            _lines = lines;
            // Where the lines of a column end: the furthest right of the page's lines that start where this one does.
            _columnRight = [];
            foreach (var page in lines.GroupBy(l => l.Page))
            {
                foreach (var line in page)
                {
                    _columnRight[line] = page.Where(l => Math.Abs(l.Box.Left - line.Box.Left) <= 2 * line.Size).Max(l => l.Box.Right);
                }
            }

            // The body size is the size most of the text is set in.
            _body = lines.Count == 0 ? 0 : lines
                .GroupBy(l => Math.Round(l.Size * 2) / 2)
                .MaxBy(g => g.Sum(l => l.Text.Length))!.Key;

            var sizes = lines.Where(IsLargeLine).Select(l => Math.Round(l.Size * 2) / 2).Distinct().OrderDescending().ToList();
            _headingLevels = [];
            var level = 0;
            for (var i = 0; i < sizes.Count; i++)
            {
                // Sizes this close are one level (a heading may be 13.9 pt on one page and 14 on another).
                if (i == 0 || sizes[i - 1] - sizes[i] > 0.75)
                {
                    level = Math.Min(level + 1, MaxHeadingLevels);
                }

                _headingLevels[sizes[i]] = level;
            }

            _boldLevel = Math.Min(level + 1, MaxHeadingLevels);

            // In a document set mostly in bold, bold says nothing about headings.
            var bodyText = lines.Where(l => Math.Abs(l.Size - _body) <= 1).ToList();
            _boldHeadings = bodyText.Sum(l => l.IsBold ? l.Text.Length : 0) < MaxBoldShare * bodyText.Sum(l => l.Text.Length);
        }

        public List<DocumentBlock> Build()
        {
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                var previous = i > 0 ? _lines[i - 1] : null;
                var next = i + 1 < _lines.Count ? _lines[i + 1] : null;
                if (HeadingLevel(line, previous, next) is { } level)
                {
                    var continues = _kind == DocumentBlockKind.Heading && _level == level && SameBlock(_last, line)
                        && _text.Length + line.Text.Length < MaxHeadingLength && _lineCount < MaxHeadingLines;
                    if (continues)
                    {
                        Append(line);
                    }
                    else
                    {
                        Start(DocumentBlockKind.Heading, line, level);
                    }
                }
                else if (line.IsRow)
                {
                    if (_kind == DocumentBlockKind.Table)
                    {
                        _text.Append('\n').Append(line.Text);
                        _last = line;
                    }
                    else
                    {
                        Start(DocumentBlockKind.Table, line);
                    }
                }
                else if (line.StartsItem || (line.MayStartItem && (_kind != DocumentBlockKind.Paragraph || !SameBlock(_last, line) || _last!.EndsSentence)))
                {
                    Start(DocumentBlockKind.ListItem, line);
                }
                else if (Continues(line))
                {
                    Append(line);
                }
                else
                {
                    Start(DocumentBlockKind.Paragraph, line);
                }
            }

            Flush();
            return _blocks;
        }

        private bool IsLargeLine(Line line) => _body > 0 && line.Size >= Math.Max(_body * HeadingScale, _body + 1) && line.Text.Length <= MaxHeadingLength;

        /// <summary>
        /// The heading level of the line, or <c>null</c> when it is body text.
        /// </summary>
        private int? HeadingLevel(Line line, Line? previous, Line? next)
        {
            if (line.IsRow || line.StartsItem || !line.Text.Any(char.IsLetter) || line.Text[^1] is ',' or ';')
            {
                return null;
            }

            if (IsLargeLine(line))
            {
                // A long passage set large is a lead paragraph, not a heading.
                var run = _lines.Count(l => l.Page == line.Page && l.Block == line.Block && Math.Abs(l.Size - line.Size) < 0.5);
                return run <= MaxHeadingLines ? _headingLevels[Math.Round(line.Size * 2) / 2] : null;
            }

            // A short bold line at body size, on its own and followed by plain text.
            var isBoldHeading = _boldHeadings && line.IsBold && Math.Abs(line.Size - _body) <= 1 && line.Text.Length <= MaxBoldHeadingLength
                && line.Text[^1] != '.'
                && (previous is null || !SameBlock(previous, line) || previous.EndsSentence || previous.IsBold != line.IsBold || previous.Size != line.Size)
                && next is not null && !next.IsBold;
            return isBoldHeading ? _boldLevel : null;
        }

        /// <summary>
        /// Whether the line goes on with the paragraph or list item being built.
        /// </summary>
        private bool Continues(Line line)
        {
            if (_last is null || _kind is not (DocumentBlockKind.Paragraph or DocumentBlockKind.ListItem))
            {
                return false;
            }

            if (SameBlock(_last, line))
            {
                if (Math.Abs(_last.Size - line.Size) > 1)
                {
                    return false;
                }

                if (_kind == DocumentBlockKind.ListItem)
                {
                    // An item goes on in lines indented like its text.
                    return line.Box.Left >= _itemLeft - 2;
                }

                // A short line that ends a sentence ends its paragraph.
                return !(_last.EndsSentence && _last.Box.Right < _columnRight[_last] - (2 * _last.Size));
            }

            // A paragraph that breaks off mid-sentence goes on in the next column or page.
            return _kind == DocumentBlockKind.Paragraph && !_last.EndsSentence && line.StartsLowercase && Math.Abs(_last.Size - line.Size) <= 1;
        }

        private static bool SameBlock(Line? a, Line b) => a is not null && a.Page == b.Page && a.Block == b.Block;

        private void Start(DocumentBlockKind kind, Line line, int level = 0)
        {
            Flush();
            _kind = kind;
            _position = line.Page;
            _level = level;
            _text.Append(line.Text);
            _itemLeft = line.TextLeft;
            _last = line;
            _lineCount = 1;
        }

        private void Append(Line line)
        {
            var text = _text.ToString();
            // A word hyphenated at the end of a line is one word ("bilgi-" + "sayar"); a hyphen after
            // a single letter ("e-" + "posta") belongs to the word.
            var hyphenated = text.Length >= 3 && text[^1] == '-' && char.IsLetter(text[^2]) && char.IsLetter(text[^3]) && line.StartsLowercase;
            if (hyphenated)
            {
                _text.Length--;
            }
            else
            {
                _text.Append(' ');
            }

            _text.Append(line.Text);
            _last = line;
            _lineCount++;
        }

        private void Flush()
        {
            if (_kind is { } kind && _text.Length > 0)
            {
                _blocks.Add(new DocumentBlock(kind, _text.ToString().Trim(), _position, _level));
            }

            _kind = null;
            _text.Clear();
        }
    }
}
