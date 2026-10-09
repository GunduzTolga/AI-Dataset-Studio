using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Versions;

/// <summary>
/// Frozen versions of datasets (SDD §14, ADR-030). A version keeps each record's content as an
/// immutable revision together with its status, position and tags; the dataset itself (the
/// "working copy") stays editable. Wherever a version id is optional, <c>null</c> means the
/// working copy.
/// </summary>
public interface IVersionService
{
    /// <summary>
    /// The dataset's versions, newest first.
    /// </summary>
    Task<IReadOnlyList<VersionInfo>> ListAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Freezes the dataset as it is now. Records without up-to-date token counts are analysed
    /// first, so the version keeps the counts its exports are checked with.
    /// </summary>
    /// <param name="datasetId">The dataset.</param>
    /// <param name="number">"1.2.0" (a leading "v" is allowed); unique within the dataset.</param>
    /// <param name="description">Optional note, e.g. what the version is for.</param>
    /// <param name="analysisProgress">Receives the number of records analysed so far.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Domain.Common.DomainException">The number is not valid or already used.</exception>
    Task<VersionInfo> CreateAsync(
        Guid datasetId,
        string number,
        string? description,
        IProgress<int>? analysisProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a version. Contents no other version keeps are removed from the file.
    /// </summary>
    Task DeleteAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The records that differ from <paramref name="olderVersionId"/> to <paramref name="newerVersionId"/>.
    /// </summary>
    Task<VersionComparison> CompareAsync(Guid datasetId, Guid? olderVersionId, Guid? newerVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A record's content in a version or the working copy; <c>null</c> if it is not there.
    /// </summary>
    Task<RecordPayload?> GetContentAsync(Guid datasetId, Guid? versionId, Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the working copy match a version as one new change: contents and statuses go back,
    /// deleted records come back and records the version does not have are archived. Nothing is
    /// lost; the result holds what is needed to undo it.
    /// </summary>
    Task<RollbackResult> RollbackAsync(Guid versionId, CancellationToken cancellationToken = default);
}
