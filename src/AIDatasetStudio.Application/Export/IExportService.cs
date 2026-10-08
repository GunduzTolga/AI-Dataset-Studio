namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Writes a dataset, or a frozen version of it, to training files (SDD §10, §13, §14). Where a
/// version id is optional, <c>null</c> means the dataset as it is now.
/// </summary>
public interface IExportService
{
    /// <summary>
    /// How many records the options would export and the first lines, without writing anything.
    /// </summary>
    /// <param name="datasetId">The dataset.</param>
    /// <param name="options">What to write and how.</param>
    /// <param name="sampleLines">How many lines to render for the preview.</param>
    /// <param name="versionId">A version of the dataset to read instead of its current records.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    Task<ExportPreview> PreviewAsync(
        Guid datasetId,
        ExportOptions options,
        int sampleLines,
        Guid? versionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the dataset. Files appear only when complete: a cancelled or failed export leaves
    /// existing files untouched. The options are remembered for the dataset and the export is
    /// added to its history. A version is written exactly as frozen: its statuses, contents and
    /// token counts, checked against its own context length.
    /// </summary>
    /// <param name="datasetId">The dataset.</param>
    /// <param name="options">What to write and how.</param>
    /// <param name="destination">The file to write, or for a split the folder to write <c>train.jsonl</c>, <c>val.jsonl</c> and <c>test.jsonl</c> into.</param>
    /// <param name="progress">Receives the records processed so far.</param>
    /// <param name="versionId">A version of the dataset to write instead of its current records.</param>
    /// <param name="cancellationToken">Cancels the export; nothing is written.</param>
    /// <exception cref="ArgumentException">The format does not fit the dataset type, or the split shares are invalid.</exception>
    Task<ExportResult> ExportAsync(
        Guid datasetId,
        ExportOptions options,
        string destination,
        IProgress<ExportProgress>? progress = null,
        Guid? versionId = null,
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
