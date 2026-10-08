namespace AIDatasetStudio.Application.Records;

/// <summary>
/// Reads records of the open workspace for lists and the editor.
/// </summary>
public interface IRecordQueryService
{
    /// <summary>
    /// Returns up to <paramref name="pageSize"/> records after <paramref name="after"/>, in dataset order.
    /// Keyset pagination keeps every page equally fast, however deep the list is scrolled.
    /// </summary>
    Task<RecordPage> GetPageAsync(
        Guid datasetId,
        RecordFilter filter,
        RecordCursor? after,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<long> CountAsync(Guid datasetId, RecordFilter filter, CancellationToken cancellationToken = default);

    /// <returns>The record, or <c>null</c> if it does not exist.</returns>
    Task<RecordDetails?> GetAsync(Guid recordId, CancellationToken cancellationToken = default);
}
