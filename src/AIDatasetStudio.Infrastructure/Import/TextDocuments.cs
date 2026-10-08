using System.Text.RegularExpressions;
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
        var start = FrontMatterLength(lines);
        var parents = new List<(int Level, string Text)>();
        (long Line, int Level, string Text)? heading = null;
        var content = new List<string>();
        string? fence = null;

        for (var i = start; i < lines.Count; i++)
        {
            var line = lines[i];
            if (fence is not null)
            {
                content.Add(line);
                if (IsFenceEnd(line, fence))
                {
                    fence = null;
                }

                continue;
            }

            if (FenceStart().Match(line) is { Success: true } opening)
            {
                fence = opening.Groups[1].Value;
                content.Add(line);
                continue;
            }

            (int Level, string Text, long Line)? next = null;
            if (AtxHeading().Match(line) is { Success: true } atx)
            {
                next = (atx.Groups[1].Length, atx.Groups[2].Value.Trim(), i + 1);
            }
            else if (SetextUnderline().Match(line) is { Success: true } underline && StartsParagraph(content, i, start, lines))
            {
                // The line above is the heading's text, not part of the previous section.
                next = (underline.Groups[1].Value[0] == '=' ? 1 : 2, lines[i - 1].Trim(), i);
                content.RemoveAt(content.Count - 1);
            }

            if (next is not { } found)
            {
                content.Add(line);
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

            parents.RemoveAll(p => p.Level >= found.Level);
            heading = (found.Line, found.Level, found.Text);
            content.Clear();
        }

        if (heading is { } last && Section(last, parents, content) is { } final)
        {
            yield return final;
        }
    }

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

    /// <summary>
    /// Whether the line above <paramref name="index"/> is a one-line paragraph of its own, which an
    /// underline turns into a heading (a "---" after a blank line is a thematic break instead).
    /// </summary>
    private static bool StartsParagraph(List<string> content, int index, int start, IReadOnlyList<string> lines)
    {
        if (index - 1 < start || content.Count == 0 || string.IsNullOrWhiteSpace(lines[index - 1]) || AtxHeading().IsMatch(lines[index - 1]))
        {
            return false;
        }

        return content.Count == 1 || string.IsNullOrWhiteSpace(content[^2]);
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
}
