using System.Text.RegularExpressions;

namespace AIDatasetStudio.Application.Documents;

/// <summary>
/// A passage of a document, sent to a model on its own.
/// </summary>
/// <param name="Number">1-based, in reading order.</param>
/// <param name="Section">The headings above the passage, joined with " › "; empty before the first heading.</param>
/// <param name="Text">The passage; headings inside it are written as Markdown headings.</param>
/// <param name="First">Where the passage starts (<see cref="DocumentBlock.Position"/>).</param>
/// <param name="Last">Where its last block starts.</param>
/// <param name="Tokens">Its length in tokens.</param>
public sealed record DocumentChunk(int Number, string Section, string Text, int First, int Last, int Tokens);

/// <summary>
/// Splits a document into passages of about a given length that follow its structure (SDD §17.5):
/// a heading starts a new passage once the current one has some substance (a title, such as a
/// page's in an archive, always does), a block never breaks
/// unless it alone is too long (then at sentences, a table at its rows), and small sections
/// share a passage. Each passage knows the headings above it, so a model reads it in context.
/// </summary>
public static partial class DocumentChunker
{
    public const int DefaultTokens = 800;
    public const int MinTokens = 200;
    public const int MaxTokens = 4_000;

    public const string SectionSeparator = " › ";

    /// <param name="targetTokens">The longest a passage should be; one sentence longer than this still makes one passage.</param>
    /// <param name="countTokens">Counts the tokens of a text.</param>
    public static IReadOnlyList<DocumentChunk> Split(IReadOnlyList<DocumentBlock> blocks, int targetTokens, Func<string, int> countTokens)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(countTokens);
        targetTokens = Math.Clamp(targetTokens, MinTokens, MaxTokens);

        // A heading starts a new passage when the current one is at least this long.
        var substance = targetTokens / 4;
        var chunks = new List<DocumentChunk>();
        var headings = new List<(int Level, string Text)>();
        var current = new List<DocumentBlock>();
        var section = string.Empty;
        var tokens = 0;
        var contentTokens = 0;

        void Close()
        {
            // Headings at the end belong to what follows them.
            var trailing = current.Count - current.FindLastIndex(b => !b.IsHeading) - 1;
            var carried = current.GetRange(current.Count - trailing, trailing);
            current.RemoveRange(current.Count - trailing, trailing);
            if (current.Count > 0)
            {
                var text = DocumentText.Join(current, headings: true);
                chunks.Add(new DocumentChunk(chunks.Count + 1, section, text, current[0].Position, current[^1].Position, countTokens(text)));
            }

            current.Clear();
            current.AddRange(carried);
            tokens = carried.Sum(b => countTokens(DocumentText.Write(b)));
            contentTokens = 0;
            section = string.Empty;
        }

        foreach (var block in blocks)
        {
            if (block.IsHeading)
            {
                // A title (of a page in an archive) always starts a passage: pages are not mixed.
                if (contentTokens >= substance || (block.Level == 0 && contentTokens > 0))
                {
                    Close();
                }

                headings.RemoveAll(h => h.Level >= block.Level);
                headings.Add((block.Level, block.Text));
                current.Add(block);
                tokens += countTokens(DocumentText.Write(block));
                continue;
            }

            foreach (var piece in Pieces(block, targetTokens, countTokens))
            {
                var size = countTokens(DocumentText.Write(piece));
                if (contentTokens > 0 && tokens + size > targetTokens)
                {
                    Close();
                }

                if (contentTokens == 0)
                {
                    section = string.Join(SectionSeparator, headings.Select(h => h.Text));
                }

                current.Add(piece);
                tokens += size;
                contentTokens += size;
            }
        }

        Close();
        return chunks;
    }

    /// <summary>
    /// The block, or parts of it that each fit: a table by rows, a list or code by lines, other
    /// text by sentences, and a sentence too long by words.
    /// </summary>
    private static IEnumerable<DocumentBlock> Pieces(DocumentBlock block, int targetTokens, Func<string, int> countTokens)
    {
        if (countTokens(DocumentText.Write(block)) <= targetTokens)
        {
            yield return block;
            yield break;
        }

        var lines = block.Text.Split('\n');
        var parts = lines.Length > 1 ? lines : Sentence().Split(block.Text);
        var separator = lines.Length > 1 ? "\n" : " ";
        foreach (var text in Pack(parts.SelectMany(p => countTokens(p) <= targetTokens ? [p] : Words(p, targetTokens, countTokens)), separator, targetTokens, countTokens))
        {
            yield return block with { Text = text };
        }
    }

    /// <summary>
    /// Joins consecutive parts while they fit.
    /// </summary>
    private static IEnumerable<string> Pack(IEnumerable<string> parts, string separator, int targetTokens, Func<string, int> countTokens)
    {
        var pack = new List<string>();
        var size = 0;
        foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var partSize = countTokens(part);
            if (pack.Count > 0 && size + partSize > targetTokens)
            {
                yield return string.Join(separator, pack);
                pack.Clear();
                size = 0;
            }

            pack.Add(part);
            size += partSize;
        }

        if (pack.Count > 0)
        {
            yield return string.Join(separator, pack);
        }
    }

    private static IEnumerable<string> Words(string text, int targetTokens, Func<string, int> countTokens) =>
        Pack(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), " ", targetTokens, countTokens);

    // After ".", "!", "?" or "…" and white space, before a capital letter, a digit or an opening quote.
    [GeneratedRegex(@"(?<=[.!?…])\s+(?=[\p{Lu}\d""“'(])")]
    private static partial Regex Sentence();
}
