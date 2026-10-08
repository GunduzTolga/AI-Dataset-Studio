using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIDatasetStudio.Application.Records;

/// <summary>
/// The record a variation was written from (SDD §17.6), kept in the variation's metadata as
/// <c>{"variantOf": {"id": …, "ordinal": …}}</c>. A record and its variations are a family: they
/// share a split part, so a question asked one way in training is not asked another way in the
/// test data, and they are not near duplicates of each other.
/// </summary>
/// <param name="Id">The family's first record (variations of a variation point at it too).</param>
/// <param name="Ordinal">Its number, for people.</param>
public sealed record VariantOf(Guid Id, long Ordinal)
{
    private const string Key = "variantOf";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The JSON path of the family's first record in metadata, for SQL (<c>json_extract</c>).
    /// </summary>
    public const string IdPath = "$.variantOf.id";

    /// <summary>
    /// The family a record belongs to: the record it is a variation of, or <c>null</c> for a record of its own.
    /// </summary>
    public static VariantOf? FromMetadata(string? metadata)
    {
        if (Parse(metadata) is not { } root || root[Key] is not JsonObject value)
        {
            return null;
        }

        try
        {
            return value.Deserialize<VariantOf>(Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The metadata of a variation written from <paramref name="source"/>: the source's own metadata
    /// (such as the document passage it came from) with the family's first record. A variation of
    /// a variation belongs to the first record's family.
    /// </summary>
    public static string For(RecordDetails source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = Parse(source.MetadataJson) ?? [];
        var family = FromMetadata(source.MetadataJson) ?? new VariantOf(source.Id, source.Ordinal);
        root[Key] = JsonSerializer.SerializeToNode(family, Options);
        return root.ToJsonString(Options);
    }

    private static JsonObject? Parse(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(metadata) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
