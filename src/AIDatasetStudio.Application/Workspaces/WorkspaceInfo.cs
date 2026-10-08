namespace AIDatasetStudio.Application.Workspaces;

/// <summary>
/// Summary of the currently open workspace.
/// </summary>
public sealed record WorkspaceInfo(
    string FilePath,
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt);
