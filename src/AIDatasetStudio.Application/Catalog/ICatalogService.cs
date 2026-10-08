using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Catalog;

/// <summary>
/// Projects and datasets of the open workspace.
/// </summary>
/// <remarks>
/// Invalid names raise <see cref="Domain.Common.DomainException"/>; unknown ids raise
/// <see cref="Common.NotFoundException"/>.
/// </remarks>
public interface ICatalogService
{
    /// <summary>
    /// All projects with their datasets and record counts, in creation order.
    /// </summary>
    Task<IReadOnlyList<ProjectSummary>> GetProjectsAsync(CancellationToken cancellationToken = default);

    Task<Guid> CreateProjectAsync(string name, string? description = null, CancellationToken cancellationToken = default);

    Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the project together with all of its datasets and records.
    /// </summary>
    Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<Guid> CreateDatasetAsync(Guid projectId, string name, DatasetType type, CancellationToken cancellationToken = default);

    Task RenameDatasetAsync(Guid datasetId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the dataset together with all of its records.
    /// </summary>
    Task DeleteDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the tokenizer used for token counts and the context length records are checked against.
    /// Changing the tokenizer makes the dataset's analysis results stale.
    /// </summary>
    Task SetTokenSettingsAsync(Guid datasetId, string tokenizer, int contextLength, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets what a model should keep in mind when it writes into the dataset's records; blank clears it.
    /// </summary>
    Task SetAIGuidanceAsync(Guid datasetId, string? guidance, CancellationToken cancellationToken = default);
}
