namespace AIDatasetStudio.Application.Workspaces;

/// <summary>
/// Creates, opens and closes the workspace file the application is working on.
/// At most one workspace is open at a time.
/// </summary>
public interface IWorkspaceService
{
    WorkspaceInfo? Current { get; }

    event EventHandler? CurrentChanged;

    /// <summary>
    /// Creates a new workspace file and makes it the current workspace.
    /// </summary>
    /// <param name="filePath">Target path.</param>
    /// <param name="name">Workspace display name.</param>
    /// <param name="overwrite">Replace an existing file (only after the user confirmed it).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="WorkspaceFileException">The file exists and <paramref name="overwrite"/> is false.</exception>
    Task<WorkspaceInfo> CreateAsync(string filePath, string name, bool overwrite = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing workspace file, upgrading its schema if it was written by an older version.
    /// </summary>
    /// <exception cref="WorkspaceFileException">The file is missing, not a workspace, or too new.</exception>
    Task<WorkspaceInfo> OpenAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes and releases the current workspace file so it can be copied or moved as a single file.
    /// </summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
