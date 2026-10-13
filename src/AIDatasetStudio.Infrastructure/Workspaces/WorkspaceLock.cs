using AIDatasetStudio.Application.Workspaces;

namespace AIDatasetStudio.Infrastructure.Workspaces;

/// <summary>
/// Keeps a workspace file from being open in two copies of the app at once (ADR-050): each copy
/// would keep its own undo history, analysis and lists over the same file, and creating a new
/// workspace in its place would delete it under the other. The lock is a file next to the
/// workspace, like SQLite's -wal and -shm ("Destek.adstudio-lock"), held open without sharing
/// while the workspace is open and deleted when it closes. The operating system lets go of it
/// when the app ends without closing, so a crash leaves nothing that keeps the file shut.
/// </summary>
internal sealed class WorkspaceLock : IDisposable
{
    public const string Suffix = "-lock";

    private readonly FileStream? _stream;

    private WorkspaceLock(string workspacePath, FileStream? stream) => (WorkspacePath, _stream) = (workspacePath, stream);

    /// <summary>
    /// The workspace file locked, as a full path.
    /// </summary>
    public string WorkspacePath { get; }

    /// <summary>
    /// Takes the lock of a workspace file. In a folder where no file can be added the workspace
    /// goes on without one, as before the lock existed.
    /// </summary>
    /// <exception cref="WorkspaceFileException">Another copy of the app has the file open (<see cref="WorkspaceFileError.InUse"/>).</exception>
    public static WorkspaceLock Acquire(string workspacePath)
    {
        var path = workspacePath + Suffix;
        try
        {
            // FileShare.None is a sharing mode on Windows and an advisory lock (flock) on Linux and macOS.
            return new WorkspaceLock(workspacePath, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose));
        }
        catch (IOException ex) when (ex.GetType() == typeof(IOException) && File.Exists(path))
        {
            throw new WorkspaceFileException(WorkspaceFileError.InUse, workspacePath, $"'{workspacePath}' is open in another copy of the application.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkspaceLock(workspacePath, null);
        }
    }

    /// <summary>
    /// Whether this is the lock of <paramref name="workspacePath"/> (a full path).
    /// </summary>
    public bool IsFor(string workspacePath) =>
        string.Equals(WorkspacePath, workspacePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose() => _stream?.Dispose();
}
