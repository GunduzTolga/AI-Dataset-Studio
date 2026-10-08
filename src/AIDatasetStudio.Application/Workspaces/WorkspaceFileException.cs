namespace AIDatasetStudio.Application.Workspaces;

public enum WorkspaceFileError
{
    NotFound,
    AlreadyExists,
    NotAWorkspace,
    CreatedByNewerVersion,
    Corrupted,

    /// <summary>The file exists but could not be read or written (locked, read-only, I/O error).</summary>
    Unavailable,
}

/// <summary>
/// A workspace file could not be created or opened. <see cref="Exception.Message"/> is English text
/// for logs; user interfaces show localized text based on <see cref="Error"/>.
/// </summary>
public sealed class WorkspaceFileException(WorkspaceFileError error, string filePath, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public WorkspaceFileError Error { get; } = error;

    public string FilePath { get; } = filePath;
}
