using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Entities;

/// <summary>
/// A homogeneous collection of records: every record in a dataset has the dataset's <see cref="Type"/>.
/// </summary>
public sealed class Dataset
{
    public const int MaxNameLength = 200;
    public const string DefaultTokenizerName = "o200k_base";
    public const int DefaultContextLength = 4_096;
    public const int MinContextLength = 256;
    public const int MaxContextLength = 2_097_152;

    private Dataset()
    {
        Name = string.Empty;
        DefaultTokenizer = DefaultTokenizerName;
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public DatasetType Type { get; private set; }

    /// <summary>
    /// Identifier of the tokenizer used for token statistics, e.g. "o200k_base".
    /// </summary>
    public string DefaultTokenizer { get; private set; }

    /// <summary>
    /// The context window training runs use; records with more tokens are flagged (SDD §7.3).
    /// </summary>
    public int ContextLength { get; private set; } = DefaultContextLength;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Dataset Create(
        Guid projectId,
        string name,
        DatasetType type,
        DateTimeOffset now,
        string? description = null,
        string defaultTokenizer = DefaultTokenizerName)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainException($"Unknown dataset type '{type}'.");
        }

        return new Dataset
        {
            Id = Guid.CreateVersion7(now),
            ProjectId = Guard.NotEmpty(projectId, nameof(projectId)),
            Name = Guard.NotBlank(name, nameof(name), MaxNameLength),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Type = type,
            DefaultTokenizer = Guard.NotBlank(defaultTokenizer, nameof(defaultTokenizer), 100),
            CreatedAt = now,
        };
    }

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), MaxNameLength);

    public void ChangeTokenSettings(string tokenizer, int contextLength)
    {
        if (contextLength is < MinContextLength or > MaxContextLength)
        {
            throw new DomainException($"The context length must be between {MinContextLength} and {MaxContextLength} tokens.");
        }

        DefaultTokenizer = Guard.NotBlank(tokenizer, nameof(tokenizer), 100);
        ContextLength = contextLength;
    }
}
