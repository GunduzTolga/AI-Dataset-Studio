using System.Text;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Infrastructure.Search;

/// <summary>
/// Turns record text and user queries into the form stored in the full-text index.
/// </summary>
/// <remarks>
/// SQLite's unicode61 tokenizer lowercases and strips diacritics (ş→s, ç→c, ğ→g, ö→o, ü→u) but
/// applies English casing to I: "ILIK" becomes "ilik" while a Turkish user types "ılık". Folding
/// I, İ and ı to "i" on both sides before indexing makes search insensitive to the dotted/dotless I
/// in either language.
/// </remarks>
internal static class SearchText
{
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                'I' or 'İ' or 'ı' => 'i',
                _ => char.ToLowerInvariant(c),
            });
        }

        return builder.ToString();
    }

    public static string ForPayload(RecordPayload payload) =>
        Normalize(string.Join('\n', payload.GetSearchableTexts()));

    /// <summary>
    /// Builds an FTS5 MATCH expression in which every word of <paramref name="query"/> must appear,
    /// as a prefix ("izi" finds "izin"). User input is always quoted, so it cannot inject FTS syntax.
    /// </summary>
    /// <returns>The expression, or <c>null</c> when the query has no searchable words.</returns>
    public static string? ToMatchExpression(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var terms = Normalize(query)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Any(char.IsLetterOrDigit))
            .Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*")
            .ToList();

        return terms.Count == 0 ? null : string.Join(' ', terms);
    }
}
