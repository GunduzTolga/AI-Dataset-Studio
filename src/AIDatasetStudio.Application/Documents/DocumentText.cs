using System.Text;

namespace AIDatasetStudio.Application.Documents;

/// <summary>
/// Writes document blocks as text, the way a passage is shown and sent to a model.
/// </summary>
public static class DocumentText
{
    /// <summary>
    /// The blocks as text: consecutive list items on their own lines (nested ones indented),
    /// other blocks separated by a blank line.
    /// </summary>
    /// <param name="headings">Whether headings are written too, as Markdown headings ("## Kargo").</param>
    public static string Join(IEnumerable<DocumentBlock> blocks, bool headings = false)
    {
        var builder = new StringBuilder();
        DocumentBlock? previous = null;
        foreach (var block in blocks)
        {
            if (block.IsHeading && !headings)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(previous?.Kind == DocumentBlockKind.ListItem && block.Kind == DocumentBlockKind.ListItem ? "\n" : "\n\n");
            }

            builder.Append(Write(block));
            previous = block;
        }

        return builder.ToString();
    }

    /// <summary>
    /// One block as text.
    /// </summary>
    public static string Write(DocumentBlock block) => block.Kind switch
    {
        DocumentBlockKind.Heading => $"{new string('#', Math.Clamp(block.Level, 1, 6))} {block.Text}",
        DocumentBlockKind.ListItem when block.Level > 0 => new string(' ', 2 * Math.Min(block.Level, 8)) + block.Text,
        _ => block.Text,
    };
}
