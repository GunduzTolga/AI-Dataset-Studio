using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Import;

public enum ImportFormat
{
    JsonLines,
    JsonArray,
    Csv,
}

/// <summary>
/// How records are laid out in the source. Column-based layouts are mapped field by field;
/// conversation layouts are converted as a whole.
/// </summary>
public enum SourceLayout
{
    /// <summary>Flat fields with unknown names; the user maps them.</summary>
    Custom,

    /// <summary><c>instruction</c> / <c>input</c> / <c>output</c>.</summary>
    Alpaca,

    /// <summary><c>prompt</c> / <c>completion</c> (OpenAI legacy fine-tuning).</summary>
    PromptCompletion,

    /// <summary><c>prompt</c> / <c>chosen</c> / <c>rejected</c>, values as text or message lists.</summary>
    HuggingFaceDpo,

    /// <summary><c>{"messages": [{"role", "content"}]}</c>.</summary>
    OpenAiMessages,

    /// <summary><c>{"conversations": [{"from", "value"}]}</c>.</summary>
    ShareGpt,

    /// <summary><c>input.messages</c> / <c>preferred_output</c> / <c>non_preferred_output</c> (OpenAI DPO).</summary>
    OpenAiPreference,
}

/// <summary>
/// A field of the target payload that a source column can be mapped to.
/// </summary>
public enum TargetField
{
    Instruction,
    Input,
    Output,
    SystemPrompt,
    User,
    Assistant,
    Prompt,
    Chosen,
    Rejected,
}

public sealed record TargetFieldInfo(TargetField Field, bool IsRequired);

/// <summary>
/// CSV reading options. <see cref="EncodingName"/> is a web name such as "utf-8" or "windows-1254".
/// </summary>
public sealed record CsvOptions(char Delimiter, string EncodingName, bool HasHeader)
{
    public const string Utf8 = "utf-8";
    public const string Windows1254 = "windows-1254";
    public const string Iso88599 = "iso-8859-9";
    public const string Windows1252 = "windows-1252";
    public const string Utf16 = "utf-16";
    public const string Utf16BigEndian = "utf-16BE";

    /// <summary>
    /// Delimiters the importer detects and offers, most common first.
    /// </summary>
    public static IReadOnlyList<char> Delimiters { get; } = [',', ';', '\t', '|'];

    /// <summary>
    /// Encodings the importer offers, as web names. Files without a byte order mark that are not
    /// valid UTF-8 are read as windows-1254 (Turkish Windows, the default of Excel in Turkey).
    /// </summary>
    public static IReadOnlyList<string> Encodings { get; } = [Utf8, Windows1254, Iso88599, Windows1252, Utf16, Utf16BigEndian];
}

/// <summary>
/// What the file looks like, with a suggested way to read it into a dataset of the requested type.
/// </summary>
public sealed record ImportAnalysis(
    string FilePath,
    ImportFormat Format,
    CsvOptions? Csv,
    SourceLayout Layout,
    IReadOnlyList<string> Columns,
    long RowCount,
    IReadOnlyDictionary<TargetField, string> SuggestedMapping,
    bool SupportsTargetType);

/// <summary>
/// Everything needed to read a file into a dataset.
/// </summary>
/// <param name="Mapping">Target field → source column, for column-based layouts.</param>
public sealed record ImportPlan(
    string FilePath,
    ImportFormat Format,
    CsvOptions? Csv,
    SourceLayout Layout,
    IReadOnlyDictionary<TargetField, string> Mapping,
    DatasetType TargetType);

public enum ImportErrorCode
{
    /// <summary>The line or item is not valid JSON.</summary>
    InvalidJson,

    /// <summary>The JSON value is not an object.</summary>
    NotAnObject,

    /// <summary>Every mapped field is empty.</summary>
    EmptyRecord,

    /// <summary>The expected conversation list is missing or has no user/assistant turns.</summary>
    NoConversation,

    /// <summary>A message has a role the dataset cannot store (e.g. "tool").</summary>
    UnknownRole,

    /// <summary>A conversation with more than one exchange cannot become an instruction record.</summary>
    MultiTurnNotSupported,

    /// <summary>A system prompt cannot be stored in an instruction record.</summary>
    SystemPromptNotSupported,

    /// <summary>The file's layout cannot be converted to the dataset's type.</summary>
    UnsupportedLayout,
}

public sealed record ImportRowError(long RowNumber, ImportErrorCode Code, string? Detail = null);

/// <summary>
/// One source row converted to a payload, or the reason it cannot be.
/// </summary>
public sealed record ImportRowResult(long RowNumber, RecordPayload? Payload, ImportRowError? Error)
{
    public bool IsValid => Payload is not null;
}

public sealed record ImportPreview(IReadOnlyList<ImportRowResult> Rows);

/// <param name="Imported">Records added to the dataset.</param>
/// <param name="Skipped">Rows that could not be converted.</param>
/// <param name="Errors">The first <see cref="MaxReportedErrors"/> skipped rows with their reasons.</param>
public sealed record ImportResult(int Imported, int Skipped, IReadOnlyList<ImportRowError> Errors)
{
    public const int MaxReportedErrors = 100;
}

public readonly record struct ImportProgress(long RowsRead, int Imported);
