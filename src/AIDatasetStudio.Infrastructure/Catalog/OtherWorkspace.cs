using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Infrastructure.Records;
using AIDatasetStudio.Infrastructure.Tags;
using AIDatasetStudio.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Catalog;

/// <summary>
/// Another workspace file, read for a merge (M23) from a private copy in a temporary folder. A file
/// open in another window is copied with SQLite's backup, so the copy is consistent, its
/// write-ahead log included. The file itself is never written, not even upgraded: only the copy is.
/// </summary>
internal sealed class OtherWorkspace : IMergeSource
{
    // SQLite result codes: the file is not a database, or a damaged one.
    private const int SqliteNotADatabase = 26;
    private const int SqliteCorrupt = 11;

    private const string FolderPrefix = "adstudio-merge-";

    private readonly string _folder;
    private readonly WorkspaceService _workspace;

    private OtherWorkspace(string fileName, string folder, WorkspaceService workspace, TimeProvider timeProvider, IReadOnlyList<ProjectSummary> projects)
    {
        FileName = fileName;
        _folder = folder;
        _workspace = workspace;
        CopyPath = workspace.Current!.FilePath;
        Projects = projects;
        Queries = new RecordQueryService(workspace);
        Tags = new TagService(workspace, timeProvider);
    }

    public string FileName { get; }

    public IReadOnlyList<ProjectSummary> Projects { get; }

    /// <summary>
    /// The private copy that is read.
    /// </summary>
    internal string CopyPath { get; }

    internal RecordQueryService Queries { get; }

    internal TagService Tags { get; }

    /// <exception cref="WorkspaceFileException">The file is missing, not a workspace, made by a newer version, or cannot be read.</exception>
    public static async Task<OtherWorkspace> OpenAsync(string path, TimeProvider timeProvider, ITokenizerProvider tokenizers, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new WorkspaceFileException(WorkspaceFileError.NotFound, fullPath, $"'{fullPath}' does not exist.");
        }

        SweepStaleCopies(timeProvider);
        var folder = Directory.CreateTempSubdirectory(FolderPrefix).FullName;
        var copy = Path.Combine(folder, Path.GetFileName(fullPath));
        var workspace = new WorkspaceService(timeProvider);
        try
        {
            Copy(fullPath, copy);
            await workspace.OpenAsync(copy, cancellationToken);
            var projects = await new CatalogService(workspace, timeProvider, tokenizers).GetProjectsAsync(cancellationToken);
            return new OtherWorkspace(Path.GetFileName(fullPath), folder, workspace, timeProvider, projects);
        }
        catch (Exception ex)
        {
            await workspace.DisposeAsync();
            Delete(folder);
            throw ex switch
            {
                // Errors name the file the user chose, not its copy.
                WorkspaceFileException file => new WorkspaceFileException(file.Error, fullPath, file.Message.Replace(copy, fullPath, StringComparison.Ordinal), file.InnerException),
                SqliteException { SqliteErrorCode: SqliteNotADatabase or SqliteCorrupt } sqlite =>
                    new WorkspaceFileException(WorkspaceFileError.NotAWorkspace, fullPath, $"'{fullPath}' is not a workspace file.", sqlite),
                SqliteException or IOException or UnauthorizedAccessException =>
                    new WorkspaceFileException(WorkspaceFileError.Unavailable, fullPath, $"'{fullPath}' could not be read.", ex),
                _ => ex,
            };
        }
    }

    // Off the UI thread: closing folds the copy's write-ahead log back. A copy that cannot be
    // closed cleanly is still deleted; it is only a copy.
    public async ValueTask DisposeAsync() => await Task.Run(async () =>
    {
        try
        {
            await _workspace.DisposeAsync();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Deleted below all the same.
        }
        finally
        {
            Delete(_folder);
        }
    });

    // A file no one has open (no write-ahead log beside it) is copied as it is: even reading it
    // read-only, SQLite would leave an empty log and index beside it. A file open elsewhere is
    // copied with SQLite's backup, which reads its log too.
    private static void Copy(string source, string destination)
    {
        var log = source + "-wal";
        if (!File.Exists(log))
        {
            var written = File.GetLastWriteTimeUtc(source);

            // Into a new file: File.Copy would carry a read-only flag over, and the copy must be written.
            using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            {
                from.CopyTo(to);
            }

            if (!File.Exists(log) && File.GetLastWriteTimeUtc(source) == written)
            {
                return;
            }

            // Opened while it was copied.
            File.Delete(destination);
        }

        Backup(source, destination);
    }

    // A consistent copy, read-only on the source; no pooled handle stays on either file.
    private static void Backup(string source, string destination)
    {
        using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);
    }

    // Copies left behind when the app was closed or crashed with a file chosen. A day old: no
    // window keeps a file chosen that long, and another instance's copies are left alone.
    private static void SweepStaleCopies(TimeProvider timeProvider)
    {
        try
        {
            var before = timeProvider.GetUtcNow().UtcDateTime.AddDays(-1);
            foreach (var folder in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(FolderPrefix + "*"))
            {
                if (folder.LastWriteTimeUtc < before)
                {
                    Delete(folder.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept the next time.
        }
    }

    private static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the temporary folder for the system to clear.
        }
    }
}
