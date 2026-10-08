namespace AIDatasetStudio.Application.Import;

public enum ImportFileError
{
    NotFound,

    /// <summary>The file exists but cannot be read (permissions, locked by another program, I/O error).</summary>
    Unreadable,

    /// <summary>A JSON array file is malformed, so its items cannot be told apart.</summary>
    InvalidJson,

    /// <summary>An Excel 97–2003 (.xls) or password-protected workbook, which cannot be read.</summary>
    LegacyWorkbook,

    /// <summary>The file is not a readable .xlsx workbook (damaged, or not a workbook at all).</summary>
    InvalidWorkbook,

    /// <summary>The chosen sheet is not in the workbook (any more).</summary>
    SheetNotFound,
}

/// <summary>
/// The source file as a whole cannot be read; problems with single rows are reported as
/// <see cref="ImportRowError"/> instead. <see cref="Exception.Message"/> is English text for logs;
/// user interfaces show localized text based on <see cref="Error"/>.
/// </summary>
/// <param name="line">1-based line of a JSON syntax error, if known.</param>
public sealed class ImportFileException(ImportFileError error, string filePath, string message, long? line = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ImportFileError Error { get; } = error;

    public string FilePath { get; } = filePath;

    public long? Line { get; } = line;
}
