using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Catalog;

public sealed record ProjectSummary(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<DatasetSummary> Datasets);

public sealed record DatasetSummary(
    Guid Id,
    Guid ProjectId,
    string Name,
    DatasetType Type,
    long RecordCount);
