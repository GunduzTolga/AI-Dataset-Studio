using System.Text.Json;

namespace AIDatasetStudio.Application.Import;

/// <summary>
/// One row of a source file, independent of the file format.
/// </summary>
public interface ISourceRow
{
    /// <summary>
    /// 1-based position in the file: line for JSONL, item for a JSON array, and for CSV the
    /// row a spreadsheet would show (the header is row 1).
    /// </summary>
    long Number { get; }

    /// <summary>
    /// The whole row as JSON; <c>null</c> for CSV.
    /// </summary>
    JsonElement? Json { get; }

    /// <summary>
    /// The raw JSON value of a column; <c>null</c> for CSV or when the column is absent.
    /// </summary>
    JsonElement? GetElement(string column);

    /// <summary>
    /// The column's value as text; <c>null</c> when the column is absent.
    /// </summary>
    string? GetText(string column);
}

/// <summary>
/// One message of a conversation log.
/// </summary>
/// <param name="Line">The 1-based line where the message starts.</param>
public sealed record TranscriptMessage(string Speaker, string Text, long Line);

/// <summary>
/// A conversation read from a log (<see cref="SourceLayout.Transcript"/>).
/// </summary>
public interface ITranscriptRow : ISourceRow
{
    IReadOnlyList<TranscriptMessage> Messages { get; }
}
