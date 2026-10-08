namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Writes a dataset to training files (SDD §10, §13).
/// </summary>
public interface IExportService
{
    /// <summary>
    /// How many records the options would export and the first lines, without writing anything.
    /// </summary>
    /// <param name="sampleLines">How many lines to render for the preview.</param>
    Task<ExportPreview> PreviewAsync(Guid datasetId, ExportOptions options, int sampleLines, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the dataset. Files appear only when complete: a cancelled or failed export leaves
    /// existing files untouched. The options are remembered for the dataset and the export is
    /// added to its history.
    /// </summary>
    /// <param name="destination">The file to write, or for a split the folder to write <c>train.jsonl</c>, <c>val.jsonl</c> and <c>test.jsonl</c> into.</param>
    /// <exception cref="ArgumentException">The format does not fit the dataset type, or the split shares are invalid.</exception>
    Task<ExportResult> ExportAsync(
        Guid datasetId,
        ExportOptions options,
        string destination,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The options of the dataset's last export, to start from; <c>null</c> if it was never exported.
    /// </summary>
    Task<ExportOptions?> GetLastOptionsAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The dataset's most recent export; <c>null</c> if it was never exported.
    /// </summary>
    Task<ExportHistoryEntry?> GetLastExportAsync(Guid datasetId, CancellationToken cancellationToken = default);
}
