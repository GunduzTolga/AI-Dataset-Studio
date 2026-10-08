using AIDatasetStudio.Domain.Common;

namespace AIDatasetStudio.Domain.Entities;

/// <summary>
/// A group of related datasets inside a workspace, e.g. "Customer Support Assistant".
/// </summary>
public sealed class Project
{
    public const int MaxNameLength = 200;

    private Project()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Project Create(string name, DateTimeOffset now, string? description = null) => new()
    {
        Id = Guid.CreateVersion7(now),
        Name = Guard.NotBlank(name, nameof(name), MaxNameLength),
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        CreatedAt = now,
    };

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), MaxNameLength);

    public void Describe(string? description) =>
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
