namespace AIDatasetStudio.Application.Catalog;

/// <summary>
/// Which datasets to merge, where, and how (SDD §21 V4, M21).
/// </summary>
/// <param name="SourceDatasetIds">Datasets of one type, from any projects; their records are copied in this order.</param>
/// <param name="TargetProjectId">The project the new dataset is created in.</param>
/// <param name="Name">The new dataset's name.</param>
/// <param name="SkipDuplicates">Copy only the first of records with exactly the same content.</param>
/// <param name="IncludeArchived">Also copy archived records (they stay archived).</param>
/// <param name="AsDrafts">Copy every record as a draft, to be reviewed again in its new dataset.</param>
public sealed record DatasetMergeRequest(
    IReadOnlyList<Guid> SourceDatasetIds,
    Guid TargetProjectId,
    string Name,
    bool SkipDuplicates = true,
    bool IncludeArchived = false,
    bool AsDrafts = false);

/// <param name="DatasetId">The new dataset.</param>
/// <param name="Copied">Records written to it.</param>
/// <param name="Duplicates">Records left out as copies of an earlier one.</param>
/// <param name="Archived">Archived records left out.</param>
public sealed record DatasetMergeResult(Guid DatasetId, long Copied, long Duplicates, long Archived);

/// <param name="Processed">Records read so far.</param>
/// <param name="Total">Records of all the sources.</param>
public readonly record struct DatasetMergeProgress(long Processed, long Total);

/// <summary>
/// Combines datasets into a new one; the sources are left as they are.
/// </summary>
public interface IDatasetMergeService
{
    /// <summary>
    /// Creates a dataset and copies the sources' records into it with their content, status,
    /// lineage, tags, metadata and dates. Variations keep their family. The new dataset takes the
    /// first source's tokenizer, context length and AI instructions.
    /// </summary>
    /// <exception cref="ArgumentException">No sources, a source of another type, or a blank name.</exception>
    /// <exception cref="Common.NotFoundException">A source or the project does not exist.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the new dataset is removed again.</exception>
    Task<DatasetMergeResult> MergeAsync(DatasetMergeRequest request, IProgress<DatasetMergeProgress>? progress = null, CancellationToken cancellationToken = default);
}
