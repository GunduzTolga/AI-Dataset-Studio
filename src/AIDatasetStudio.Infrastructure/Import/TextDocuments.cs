using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// A part of a text document that becomes one row: its first line in the file and its column values.
/// </summary>
internal sealed record TextRow(long Line, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Splits Markdown documents into sections at their headings and plain text into paragraphs
/// (SDD §12.2). Both give rows with fixed columns (<see cref="TextColumns"/>).
/// </summary>
internal static partial class TextDocuments
{
    public const string PathSeparator = " › ";

    // Front matter is looked for only this far, so a thematic break at the top is not taken for it.
    private const int MaxFrontMatterLines = 200;

    /// <summary>
    /// One row per heading with text under it: the heading, the headings above it and the text up
    /// to the next heading. Text before the first heading and headings without text give no row.
    /// "#" lines in fenced code are not headings; setext headings (underlined with = or -) and
    /// YAML front matter are understood.
    /// </summary>
    public static IEnumerable<TextRow> Sections(IReadOnlyList<string> lines)
    {
        var parents = new List<(int Level, string Text)>();
        (long Line, int Level, string Text)? heading = null;
        var content = new List<string>();

        foreach (var element in Elements(lines))
        {
            if (element.Level is not { } level)
            {
                content.Add(element.Text);
                continue;
            }

            if (heading is { } current && Section(current, parents, content) is { } row)
            {
                yield return row;
            }

            // The new heading's parents are the open headings of higher levels.
            if (heading is { } previous)
            {
                parents.Add((previous.Level, previous.Text));
            }

            parents.RemoveAll(p => p.Level >= level);
            heading = (element.Line, level, element.Text);
            content.Clear();
        }

        if (heading is { } last && Section(last, parents, content) is { } final)
        {
            yield return final;
        }
    }

    /// <summary>
    /// A Markdown document as blocks: its headings, and the runs of lines between blank lines
    /// (fenced code stays whole) as paragraphs, lists or tables, with their lines kept.
    /// </summary>
    public static IEnumerable<DocumentBlock> MarkdownBlocks(IReadOnlyList<string> lines)
    {
        var run = new List<string>();
        long first = 0;
        var fenced = false;
        foreach (var element in Elements(lines))
        {
            if (element.Level is { } level)
            {
                if (Run(run, first) is { } block)
                {
                    yield return block;
                }

                if (element.Text.Length > 0)
                {
                    yield return new DocumentBlock(DocumentBlockKind.Heading, element.Text, (int)element.Line, level);
                }

                continue;
            }

            // A blank line ends a run, unless it is inside fenced code.
            if (string.IsNullOrWhiteSpace(element.Text) && !(element.InFence && fenced))
            {
                if (Run(run, first) is { } block)
                {
                    yield return block;
                }

                continue;
            }

            if (run.Count == 0)
            {
                first = element.Line;
            }

            fenced = element.InFence;
            run.Add(element.Text.TrimEnd());
        }

        if (Run(run, first) is { } final)
        {
            yield return final;
        }
    }

    /// <summary>
    /// Plain text as one paragraph block per run of lines between blank lines.
    /// </summary>
    public static IEnumerable<DocumentBlock> ParagraphBlocks(IEnumerable<string> lines) =>
        Paragraphs(lines).Select(row => new DocumentBlock(DocumentBlockKind.Paragraph, row.Values[TextColumns.Paragraph], (int)row.Line));

    /// <summary>
    /// The sections of a document read as blocks (PDF, Word), like <see cref="Sections(IReadOnlyList{string})"/>.
    /// </summary>
    public static IEnumerable<TextRow> Sections(IEnumerable<DocumentBlock> blocks)
    {
        var parents = new List<(int Level, string Text)>();
        (long Line, int Level, string Text)? heading = null;
        var content = new List<DocumentBlock>();
        foreach (var block in blocks)
        {
            if (!block.IsHeading)
            {
                content.Add(block);
                continue;
            }

            if (heading is { } current && Section(current, parents, [DocumentText.Join(content)]) is { } row)
            {
                yield return row;
            }

            if (heading is { } previous)
            {
                parents.Add((previous.Level, previous.Text));
            }

            parents.RemoveAll(p => p.Level >= block.Level);
            heading = (block.Position, block.Level, block.Text);
            content.Clear();
        }

        if (heading is { } last && Section(last, parents, [DocumentText.Join(content)]) is { } final)
        {
            yield return final;
        }
    }

    /// <summary>
    /// One row per paragraph, list item or table of a document read as blocks.
    /// </summary>
    public static IEnumerable<TextRow> Paragraphs(IEnumerable<DocumentBlock> blocks) =>
        blocks.Where(b => !b.IsHeading).Select(b => Paragraph(b.Position, [.. b.Text.Split('\n')]));

    /// <summary>
    /// One row per paragraph (lines between blank lines): its first line, the lines after it, and
    /// the whole paragraph.
    /// </summary>
    public static IEnumerable<TextRow> Paragraphs(IEnumerable<string> lines)
    {
        var paragraph = new List<string>();
        long number = 0;
        long first = 0;
        foreach (var line in lines)
        {
            number++;
            if (!string.IsNullOrWhiteSpace(line))
            {
                if (paragraph.Count == 0)
                {
                    first = number;
                }

                paragraph.Add(line.TrimEnd());
                continue;
            }

            if (paragraph.Count > 0)
            {
                yield return Paragraph(first, paragraph);
                paragraph.Clear();
            }
        }

        if (paragraph.Count > 0)
        {
            yield return Paragraph(first, paragraph);
        }
    }

    private static TextRow Paragraph(long line, List<string> lines) => new(line, new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TextColumns.FirstLine] = lines[0].Trim(),
        [TextColumns.Rest] = string.Join('\n', lines.Skip(1)),
        [TextColumns.Paragraph] = string.Join('\n', lines),
    });

    private static TextRow? Section((long Line, int Level, string Text) heading, List<(int Level, string Text)> parents, List<string> content)
    {
        var first = content.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        if (first < 0)
        {
            return null;
        }

        var last = content.FindLastIndex(l => !string.IsNullOrWhiteSpace(l));
        var above = parents.Select(p => p.Text).Where(t => t.Length > 0).ToList();
        return new TextRow(heading.Line, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TextColumns.Heading] = heading.Text,
            [TextColumns.HeadingPath] = string.Join(PathSeparator, heading.Text.Length > 0 ? above.Append(heading.Text) : above),
            [TextColumns.ParentHeadings] = string.Join(PathSeparator, above),
            [TextColumns.Content] = string.Join('\n', content.Skip(first).Take(last - first + 1).Select(l => l.TrimEnd())),
        });
    }

    private static DocumentBlock? Run(List<string> run, long first)
    {
        if (run.Count == 0)
        {
            return null;
        }

        var kind = run.All(l => l.TrimStart().StartsWith('|')) ? DocumentBlockKind.Table
            : MarkdownListItem().IsMatch(run[0]) ? DocumentBlockKind.ListItem
            : DocumentBlockKind.Paragraph;
        var block = new DocumentBlock(kind, string.Join('\n', run), (int)first);
        run.Clear();
        return block;
    }

    /// <summary>
    /// The lines of a Markdown document after its front matter: headings with their level, and
    /// every other line (blank ones too) with whether it is in fenced code.
    /// </summary>
    private static IEnumerable<(int? Level, string Text, long Line, bool InFence)> Elements(IReadOnlyList<string> lines)
    {
        var start = FrontMatterLength(lines);
        string? fence = null;
        var afterHeading = true;
        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (fence is not null)
            {
                yield return (null, line, i + 1, true);
                if (IsFenceEnd(line, fence))
                {
                    fence = null;
                }

                afterHeading = false;
                continue;
            }

            if (FenceStart().Match(line) is { Success: true } opening)
            {
                fence = opening.Groups[1].Value;
                afterHeading = false;
                yield return (null, line, i + 1, true);
                continue;
            }

            if (AtxHeading().Match(line) is { Success: true } atx)
            {
                afterHeading = true;
                yield return (atx.Groups[1].Length, atx.Groups[2].Value.Trim(), i + 1, false);
                continue;
            }

            // A one-line paragraph of its own, underlined, is a heading (a "---" after a blank
            // line is a thematic break instead).
            if (i + 1 < lines.Count && SetextUnderline().Match(lines[i + 1]) is { Success: true } underline
                && !string.IsNullOrWhiteSpace(line) && (afterHeading || i == start || string.IsNullOrWhiteSpace(lines[i - 1])))
            {
                afterHeading = true;
                yield return (underline.Groups[1].Value[0] == '=' ? 1 : 2, line.Trim(), i + 1, false);
                i++;
                continue;
            }

            afterHeading = false;
            yield return (null, line, i + 1, false);
        }
    }

    /// <summary>
    /// Lines taken by a YAML front matter block ("---" … "---" or "...") at the top, or 0.
    /// </summary>
    private static int FrontMatterLength(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].TrimEnd() != "---")
        {
            return 0;
        }

        for (var i = 1; i < Math.Min(lines.Count, MaxFrontMatterLines); i++)
        {
            if (lines[i].TrimEnd() is "---" or "...")
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static bool IsFenceEnd(string line, string fence)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]) && line.Length - line.TrimStart().Length <= 3;
    }

    // Up to three spaces of indentation, as CommonMark allows.
    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(```+|~~~+)")]
    private static partial Regex FenceStart();

    [GeneratedRegex(@"^ {0,3}(=+|-+)[ \t]*$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex(@"^ {0,3}(?:[-*+]|\d{1,9}[.)])[ \t]+")]
    private static partial Regex MarkdownListItem();
}
