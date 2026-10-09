using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Catalog;

public sealed record ProjectSummary(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<DatasetSummary> Datasets);

/// <param name="Tokenizer">Tokenizer for token counts, e.g. "o200k_base".</param>
/// <param name="ContextLength">Target context length; records with more tokens are flagged.</param>
/// <param name="LatestVersion">The number of the newest version, e.g. "1.2.0"; <c>null</c> without versions.</param>
/// <param name="AIGuidance">What a model should keep in mind when writing into the records.</param>
public sealed record DatasetSummary(
    Guid Id,
    Guid ProjectId,
    string Name,
    DatasetType Type,
    long RecordCount,
    string Tokenizer = Domain.Entities.Dataset.DefaultTokenizerName,
    int ContextLength = Domain.Entities.Dataset.DefaultContextLength,
    string? LatestVersion = null,
    string? AIGuidance = null);
