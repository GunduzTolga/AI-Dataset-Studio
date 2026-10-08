using System.Globalization;
using System.Text;

namespace AIDatasetStudio.Application.Versions;

/// <summary>
/// A piece of text that is in both versions, only in the old one or only in the new one.
/// </summary>
public readonly record struct DiffSegment(DiffKind Kind, string Text);

/// <summary>
/// Word-level differences between two texts, for showing changes in colour (SDD §14.2).
/// The old text is the <see cref="DiffKind.Equal"/> and <see cref="DiffKind.Deleted"/> segments
/// in order, the new text the <see cref="DiffKind.Equal"/> and <see cref="DiffKind.Inserted"/> ones.
/// </summary>
public sealed class TextDiff
{
    private TextDiff(IReadOnlyList<DiffSegment> segments) => Segments = segments;

    public IReadOnlyList<DiffSegment> Segments { get; }

    public bool HasChanges => Segments.Any(s => s.Kind != DiffKind.Equal);

    /// <summary>
    /// The old text: unchanged and deleted parts.
    /// </summary>
    public IReadOnlyList<DiffSegment> OldSide => [.. Segments.Where(s => s.Kind != DiffKind.Inserted)];

    /// <summary>
    /// The new text: unchanged and inserted parts.
    /// </summary>
    public IReadOnlyList<DiffSegment> NewSide => [.. Segments.Where(s => s.Kind != DiffKind.Deleted)];

    public static TextDiff Compute(string? oldText, string? newText)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return new TextDiff(oldText.Length == 0 ? [] : [new DiffSegment(DiffKind.Equal, oldText)]);
        }

        var oldTokens = Tokenize(oldText);
        var newTokens = Tokenize(newText);
        var steps = SequenceDiff.Compute(oldTokens, newTokens, StringComparer.Ordinal);
        return new TextDiff(Merge(Group(steps, oldTokens, newTokens)));
    }

    /// <summary>
    /// Words (letters, digits and their marks), runs of spaces, line breaks and single other
    /// characters; a surrogate pair stays one token.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            if (IsWordChar(c))
            {
                while (i < text.Length && IsWordChar(text[i]))
                {
                    i++;
                }
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i += 2;
            }
            else if (c is '\n' or '\r')
            {
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] is not ('\n' or '\r'))
                {
                    i++;
                }
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i += 2;
            }
            else
            {
                i++;
            }

            tokens.Add(text[start..i]);
        }

        return tokens;
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    // Equal runs become one segment; each run of changes becomes its deleted part, then its inserted part.
    private static List<DiffSegment> Group(IReadOnlyList<DiffStep> steps, List<string> oldTokens, List<string> newTokens)
    {
        var segments = new List<DiffSegment>();
        var equal = new StringBuilder();
        var deleted = new StringBuilder();
        var inserted = new StringBuilder();

        void FlushChange()
        {
            if (deleted.Length > 0)
            {
                segments.Add(new DiffSegment(DiffKind.Deleted, deleted.ToString()));
                deleted.Clear();
            }

            if (inserted.Length > 0)
            {
                segments.Add(new DiffSegment(DiffKind.Inserted, inserted.ToString()));
                inserted.Clear();
            }
        }

        void FlushEqual()
        {
            if (equal.Length > 0)
            {
                segments.Add(new DiffSegment(DiffKind.Equal, equal.ToString()));
                equal.Clear();
            }
        }

        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case DiffKind.Equal:
                    FlushChange();
                    equal.Append(oldTokens[step.OldIndex]);
                    break;
                case DiffKind.Deleted:
                    FlushEqual();
                    deleted.Append(oldTokens[step.OldIndex]);
                    break;
                default:
                    FlushEqual();
                    inserted.Append(newTokens[step.NewIndex]);
                    break;
            }
        }

        FlushChange();
        FlushEqual();
        return segments;
    }

    /// <summary>
    /// Spaces between two changes join them ("[the quick] fox" rather than "[the] [quick] fox"),
    /// so a rewritten phrase reads as one change; line breaks keep changes apart.
    /// </summary>
    private static List<DiffSegment> Merge(List<DiffSegment> segments)
    {
        var merged = new List<DiffSegment>(segments.Count);
        var i = 0;
        while (i < segments.Count)
        {
            if (segments[i].Kind == DiffKind.Equal)
            {
                merged.Add(segments[i++]);
                continue;
            }

            // A run of changes, possibly joined across spaces-only equal parts.
            var deleted = new StringBuilder();
            var inserted = new StringBuilder();
            while (i < segments.Count)
            {
                var segment = segments[i];
                if (segment.Kind == DiffKind.Deleted)
                {
                    deleted.Append(segment.Text);
                }
                else if (segment.Kind == DiffKind.Inserted)
                {
                    inserted.Append(segment.Text);
                }
                else if (IsJoinable(segment.Text) && i + 1 < segments.Count && segments[i + 1].Kind != DiffKind.Equal)
                {
                    deleted.Append(segment.Text);
                    inserted.Append(segment.Text);
                }
                else
                {
                    break;
                }

                i++;
            }

            if (deleted.Length > 0)
            {
                merged.Add(new DiffSegment(DiffKind.Deleted, deleted.ToString()));
            }

            if (inserted.Length > 0)
            {
                merged.Add(new DiffSegment(DiffKind.Inserted, inserted.ToString()));
            }
        }

        return merged;
    }

    private static bool IsJoinable(string text) => text.All(c => char.IsWhiteSpace(c) && c is not ('\n' or '\r'));
}
