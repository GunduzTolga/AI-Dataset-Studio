using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIDatasetStudio.Application.Documents;

/// <summary>
/// The passage of a document a record was written from. It is kept in the record's metadata,
/// so a reviewer can check the answer against what the document says (SDD §17.5).
/// </summary>
/// <param name="File">The document's file name.</param>
/// <param name="Section">The headings above the passage, joined with " › "; empty when there are none.</param>
/// <param name="Unit">The unit of <paramref name="First"/> and <paramref name="Last"/>: "page", "paragraph" or "line".</param>
/// <param name="Passage">The passage the model read.</param>
public sealed record PassageSource(string File, string Section, string Unit, int First, int Last, string Passage)
{
    private const string Key = "source";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep Turkish text readable in the database, like record contents; this JSON is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PassageSource From(SourceDocument document, DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunk);
        return new PassageSource(document.FileName, chunk.Section, document.PositionUnit, chunk.First, chunk.Last, chunk.Text);
    }

    /// <summary>
    /// The record metadata holding this source: <c>{"source": {…}}</c>.
    /// </summary>
    public string ToMetadata() => new JsonObject { [Key] = JsonSerializer.SerializeToNode(this, Options) }.ToJsonString(Options);

    /// <summary>
    /// The source kept in a record's metadata, or <c>null</c> when it has none.
    /// </summary>
    public static PassageSource? FromMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(Key, out var source)
                ? source.Deserialize<PassageSource>(Options)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
