namespace AIDatasetStudio.Application.Workspaces;

/// <summary>
/// Facts about the portable workspace file format (SDD §3.1, ADR-001).
/// </summary>
public static class WorkspaceFile
{
    /// <summary>
    /// File extension, including the leading dot. Kept in one place so it can be renamed easily.
    /// </summary>
    public const string Extension = ".adstudio";

    public const string DisplayName = "AI Dataset Studio Workspace";
}
