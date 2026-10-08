using AIDatasetStudio.Domain.Common;

namespace AIDatasetStudio.Domain.Entities;

/// <summary>
/// Metadata of the workspace. One portable workspace file holds exactly one workspace (SDD §3.1).
/// </summary>
public sealed class Workspace
{
    public const int MaxNameLength = 200;

    private Workspace()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Workspace Create(string name, DateTimeOffset now, string? description = null) => new()
    {
        Id = Guid.CreateVersion7(now),
        Name = Guard.NotBlank(name, nameof(name), MaxNameLength),
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        CreatedAt = now,
    };

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), MaxNameLength);
}
