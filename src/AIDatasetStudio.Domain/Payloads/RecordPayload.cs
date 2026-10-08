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
