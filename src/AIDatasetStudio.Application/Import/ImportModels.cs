using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Import;

public enum ImportFormat
{
    JsonLines,
    JsonArray,
    Csv,

    /// <summary>An Excel workbook (.xlsx, .xlsm): one sheet or all of them.</summary>
    Excel,

    /// <summary>A Markdown document split into sections at its headings.</summary>
    Markdown,

    /// <summary>Plain text split into paragraphs at blank lines.</summary>
    PlainText,

    /// <summary>A PDF document, read as sections at its headings or as paragraphs.</summary>
    Pdf,

    /// <summary>A Word document (.docx), read as sections at its headings or as paragraphs.</summary>
    Word,

    /// <summary>An HTML page, read as sections at its headings or as paragraphs.</summary>
    Html,

    /// <summary>A ZIP of pages (a Notion or Confluence export), read as sections under each page's title.</summary>
    Archive,

    /// <summary>A conversation log: a WhatsApp export or a transcript with a speaker before each message.</summary>
    Transcript,

    /// <summary>An Apache Parquet file (Hugging Face datasets), read row by row as JSON objects (M25).</summary>
    Parquet,
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

    /// <summary>Document sections (Markdown, PDF, Word): heading, heading path, parent headings and content.</summary>
    Sections,

    /// <summary>Paragraphs of plain text or a document: first line, the rest and the whole paragraph.</summary>
    Paragraphs,

    /// <summary>Conversations of a log, message by message; its speakers are mapped to roles.</summary>
    Transcript,
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

    /// <summary>The thinking before the answer (ADR-052).</summary>
    Reasoning,
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
/// Which sheets of a workbook are read, and whether their first row holds column names.
/// </summary>
/// <param name="Sheet">A sheet name, or <c>null</c> for every sheet one after the other.</param>
public sealed record SheetOptions(string? Sheet, bool HasHeader);

/// <summary>
/// Settings chosen by the user instead of the detected ones; <c>null</c> parts are detected.
/// </summary>
/// <param name="Format">How to read the file; one of <see cref="ImportAnalysis.ReadableAs"/>.</param>
public sealed record ImportOverrides(ImportFormat? Format = null, CsvOptions? Csv = null, SheetOptions? Sheet = null);

/// <summary>
/// Column names of the rows read from Markdown and plain-text files. Interfaces show localized
/// names for them.
/// </summary>
public static class TextColumns
{
    /// <summary>The section's heading.</summary>
    public const string Heading = "heading";

    /// <summary>The headings above the section and its own, joined with " › ".</summary>
    public const string HeadingPath = "heading_path";

    /// <summary>The headings above the section, joined with " › ".</summary>
    public const string ParentHeadings = "parent_headings";

    /// <summary>The section's text without its heading.</summary>
    public const string Content = "content";

    /// <summary>The first line of a paragraph.</summary>
    public const string FirstLine = "first_line";

    /// <summary>The lines of a paragraph after the first.</summary>
    public const string Rest = "rest";

    /// <summary>The whole paragraph.</summary>
    public const string Paragraph = "paragraph";

    public static IReadOnlyList<string> Markdown { get; } = [Heading, HeadingPath, ParentHeadings, Content];

    public static IReadOnlyList<string> PlainText { get; } = [FirstLine, Rest, Paragraph];
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
    bool SupportsTargetType)
{
    /// <summary>
    /// The sheets read from a workbook; <c>null</c> for other formats.
    /// </summary>
    public SheetOptions? Sheet { get; init; }

    /// <summary>
    /// The workbook's visible sheets in tab order; empty for other formats.
    /// </summary>
    public IReadOnlyList<string> Sheets { get; init; } = [];

    /// <summary>
    /// The formats the user can choose between for this file; only <see cref="Format"/> when
    /// there is no other sensible reading.
    /// </summary>
    public IReadOnlyList<ImportFormat> ReadableAs { get; init; } = [Format];
}

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
    DatasetType TargetType,
    SheetOptions? Sheet = null);

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

/// <param name="Sheet">The workbook sheet of the row when several sheets are read.</param>
public sealed record ImportRowError(long RowNumber, ImportErrorCode Code, string? Detail = null, string? Sheet = null);

/// <summary>
/// One source row converted to a payload, or the reason it cannot be.
/// </summary>
/// <param name="Sheet">The workbook sheet of the row when several sheets are read.</param>
public sealed record ImportRowResult(long RowNumber, RecordPayload? Payload, ImportRowError? Error, string? Sheet = null)
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
