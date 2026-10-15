using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Workspaces;

internal sealed class WorkspaceService(TimeProvider timeProvider) : IWorkspaceService, IWorkspaceDatabase, IAsyncDisposable
{
    // SQLite result codes: https://www.sqlite.org/rescode.html
    private const int SqliteNotADatabase = 26;
    private const int SqliteCorrupt = 11;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private WorkspaceLock? _lock;
    private int _disposed;

    public WorkspaceInfo? Current { get; private set; }

    public event EventHandler? CurrentChanged;

    public async Task<WorkspaceInfo> CreateAsync(string filePath, string name, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        var workspace = Workspace.Create(name, timeProvider.GetUtcNow());

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(fullPath) && !overwrite)
            {
                throw new WorkspaceFileException(WorkspaceFileError.AlreadyExists, fullPath, $"'{fullPath}' already exists.");
            }

            // Before anything is deleted: the file being replaced may be open in another copy of the app.
            var fileLock = LockFor(fullPath);
            try
            {
                // Close first: the file being replaced may be the one that is currently open.
                await CloseCoreAsync(cancellationToken, keep: fileLock);
                DeleteWorkspaceFiles(fullPath);
            }
            catch
            {
                ReleaseUnlessCurrent(fileLock);
                throw;
            }

            var connectionString = WorkspaceDatabase.BuildConnectionString(fullPath, allowCreate: true);
            try
            {
                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    // Both settings are persisted in the file itself.
                    await WorkspaceDatabase.ExecuteAsync(connection, $"PRAGMA application_id = {WorkspaceDatabase.ApplicationId};", cancellationToken);
                    await WorkspaceDatabase.ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
                }

                await using (var context = new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString)))
                {
                    await context.Database.MigrateAsync(cancellationToken);
                    context.Workspace.Add(workspace);
                    await context.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                SqliteConnection.ClearPool(new SqliteConnection(connectionString));
                DeleteWorkspaceFiles(fullPath);
                ReleaseUnlessCurrent(fileLock);
                if (ex is SqliteException sqliteException)
                {
                    throw Unavailable(fullPath, sqliteException);
                }

                throw;
            }

            return SetCurrent(fullPath, connectionString, workspace, fileLock);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceInfo> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(fullPath))
            {
                throw new WorkspaceFileException(WorkspaceFileError.NotFound, fullPath, $"'{fullPath}' does not exist.");
            }

            // Before the file is read: opening upgrades and repairs it, which another copy of the app must not see.
            var fileLock = LockFor(fullPath);
            var connectionString = WorkspaceDatabase.BuildConnectionString(fullPath, allowCreate: false);
            try
            {
                // Validate before closing the current workspace, so a failed open leaves it open.
                await EnsureIsWorkspaceFileAsync(fullPath, connectionString, cancellationToken);
                await using (var probe = new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString)))
                {
                    await EnsureNotNewerThanAppAsync(fullPath, probe, cancellationToken);
                }

                await CloseCoreAsync(cancellationToken, keep: fileLock);

                Workspace workspace;
                await using (var context = new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString)))
                {
                    await context.Database.MigrateAsync(cancellationToken);

                    workspace = await context.Workspace.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
                        ?? throw new WorkspaceFileException(WorkspaceFileError.Corrupted, fullPath, $"'{fullPath}' has no workspace metadata.");
                }

                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    await SearchIndex.RepairAsync(connection, cancellationToken);
                }

                return SetCurrent(fullPath, connectionString, workspace, fileLock);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteNotADatabase or SqliteCorrupt)
            {
                SqliteConnection.ClearPool(new SqliteConnection(connectionString));
                ReleaseUnlessCurrent(fileLock);
                throw new WorkspaceFileException(WorkspaceFileError.NotAWorkspace, fullPath, $"'{fullPath}' is not an {WorkspaceFile.DisplayName} file.", ex);
            }
            catch (Exception ex)
            {
                SqliteConnection.ClearPool(new SqliteConnection(connectionString));
                ReleaseUnlessCurrent(fileLock);
                if (ex is SqliteException sqliteException)
                {
                    throw Unavailable(fullPath, sqliteException);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CloseCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The DI container disposes this instance once per interface it is registered under, so
    // disposal must be idempotent.
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await CloseAsync();
        _gate.Dispose();
    }

    /// <summary>
    /// Creates a context over the current workspace. Callers own and dispose it.
    /// </summary>
    public WorkspaceDbContext CreateDbContext() =>
        new(WorkspaceDatabase.BuildOptions(RequireConnectionString()));

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(RequireConnectionString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private string RequireConnectionString() =>
        _connectionString ?? throw new InvalidOperationException("No workspace is open.");

    private static async Task EnsureIsWorkspaceFileAsync(string fullPath, string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var applicationId = await WorkspaceDatabase.ExecuteScalarAsync(connection, "PRAGMA application_id;", cancellationToken);
        if (applicationId != WorkspaceDatabase.ApplicationId)
        {
            throw new WorkspaceFileException(WorkspaceFileError.NotAWorkspace, fullPath, $"'{fullPath}' is not an {WorkspaceFile.DisplayName} file.");
        }
    }

    // Opening a file written by a newer version would let old code write into a schema it does not know.
    private static async Task EnsureNotNewerThanAppAsync(string fullPath, WorkspaceDbContext context, CancellationToken cancellationToken)
    {
        var known = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (applied.Any(migration => !known.Contains(migration)))
        {
            throw new WorkspaceFileException(
                WorkspaceFileError.CreatedByNewerVersion,
                fullPath,
                $"'{fullPath}' was saved by a newer version of AI Dataset Studio. Please update the application.");
        }
    }

    // The open workspace's own lock when the same file is opened or replaced again.
    private WorkspaceLock LockFor(string fullPath) =>
        _lock is { } current && current.IsFor(fullPath) ? current : WorkspaceLock.Acquire(fullPath);

    // After a failed open or create: a lock taken for it goes, the open workspace's stays.
    private void ReleaseUnlessCurrent(WorkspaceLock fileLock)
    {
        if (!ReferenceEquals(fileLock, _lock))
        {
            fileLock.Dispose();
        }
    }

    private WorkspaceInfo SetCurrent(string fullPath, string connectionString, Workspace workspace, WorkspaceLock fileLock)
    {
        _connectionString = connectionString;
        _lock = fileLock;
        Current = new WorkspaceInfo(fullPath, workspace.Id, workspace.Name, workspace.Description, workspace.CreatedAt);
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken, WorkspaceLock? keep = null)
    {
        if (_connectionString is not { } connectionString)
        {
            return;
        }

        // Fold the WAL back into the main file and release every pooled handle, so the
        // workspace is a single self-contained file again (no -wal / -shm side files).
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await WorkspaceDatabase.ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
            SqliteConnection.ClearPool(connection);
        }

        // Released once the file is whole again; kept when the same file is being opened or replaced.
        if (!ReferenceEquals(_lock, keep))
        {
            _lock?.Dispose();
        }

        _lock = null;
        _connectionString = null;
        Current = null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    private static WorkspaceFileException Unavailable(string fullPath, SqliteException ex) =>
        new(WorkspaceFileError.Unavailable, fullPath, $"'{fullPath}' could not be used: {ex.Message}", ex);

    private static void DeleteWorkspaceFiles(string fullPath)
    {
        foreach (var path in new[] { fullPath, fullPath + "-wal", fullPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
