using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Import;

/// <summary>
/// Reads JSONL, JSON array and CSV files into datasets (SDD §12.1).
/// </summary>
public interface IImportService
{
    /// <summary>
    /// File extensions the importer reads, with the leading dot.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Detects the format, CSV settings, layout and columns, counts the rows and suggests a
    /// mapping for <paramref name="targetType"/>.
    /// </summary>
    /// <param name="filePath">The file to read.</param>
    /// <param name="targetType">Type of the dataset that will receive the records.</param>
    /// <param name="csvOverride">CSV settings chosen by the user instead of the detected ones.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    Task<ImportAnalysis> AnalyzeAsync(
        string filePath,
        DatasetType targetType,
        CsvOptions? csvOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts the first <paramref name="maxRows"/> rows without writing anything.
    /// </summary>
    Task<ImportPreview> PreviewAsync(ImportPlan plan, int maxRows = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds every convertible row to the dataset in one transaction; rows that cannot be
    /// converted are skipped and reported. Cancelling rolls everything back.
    /// </summary>
    Task<ImportResult> ImportAsync(
        ImportPlan plan,
        Guid datasetId,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
