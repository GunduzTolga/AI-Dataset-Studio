using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// Model-agnostic content of a single record (SDD §4, ADR-003). Payloads are immutable;
/// editing a record replaces its payload.
/// </summary>
public abstract record RecordPayload
{
    public abstract DatasetType Type { get; }

    /// <summary>
    /// The content fields, in a fixed order, that define the record's identity for hashing.
    /// </summary>
    protected abstract IEnumerable<string?> GetCanonicalParts();

    /// <summary>
    /// All human-readable text of the record, used to build the full-text search index.
    /// </summary>
    public abstract IEnumerable<string> GetSearchableTexts();

    /// <summary>
    /// The text that best identifies the record in a list (e.g. the instruction or first user turn).
    /// </summary>
    protected abstract string? GetPreviewSource();

    /// <summary>
    /// A single-line summary for record lists: whitespace collapsed, cut to <paramref name="maxLength"/>.
    /// </summary>
    public string GetPreviewText(int maxLength = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

        var source = GetPreviewSource();
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= maxLength)
        {
            return collapsed;
        }

        // Never cut between the two halves of a surrogate pair (emoji and other non-BMP characters).
        var cut = maxLength - 1;
        if (char.IsHighSurrogate(collapsed[cut - 1]))
        {
            cut--;
        }

        return string.Concat(collapsed.AsSpan(0, cut).TrimEnd(), "…");
    }

    /// <summary>
    /// Lowercase hex SHA-256 over the exact content (no normalization). Used for exact
    /// duplicate detection and, later, for content-addressed version snapshots.
    /// </summary>
    public string ComputeContentHash()
    {
        var builder = new StringBuilder();
        AppendPart(builder, ((int)Type).ToString(CultureInfo.InvariantCulture));
        foreach (var part in GetCanonicalParts())
        {
            AppendPart(builder, part);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    // Length-prefixing keeps ("ab", "c") and ("a", "bc") distinct, and null distinct from "".
    private static void AppendPart(StringBuilder builder, string? part)
    {
        if (part is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
    }
}
