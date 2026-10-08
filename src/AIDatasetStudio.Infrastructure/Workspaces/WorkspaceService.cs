using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Workspaces;

internal sealed class WorkspaceService(TimeProvider timeProvider) : IWorkspaceService, IAsyncDisposable
{
    // SQLite result codes: https://www.sqlite.org/rescode.html
    private const int SqliteNotADatabase = 26;
    private const int SqliteCorrupt = 11;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;

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

            // Close first: the file being replaced may be the one that is currently open.
            await CloseCoreAsync(cancellationToken);
            DeleteWorkspaceFiles(fullPath);

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
                if (ex is SqliteException sqliteException)
                {
                    throw Unavailable(fullPath, sqliteException);
                }

                throw;
            }

            return SetCurrent(fullPath, connectionString, workspace);
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

            var connectionString = WorkspaceDatabase.BuildConnectionString(fullPath, allowCreate: false);
            try
            {
                // Validate before closing the current workspace, so a failed open leaves it open.
                await EnsureIsWorkspaceFileAsync(fullPath, connectionString, cancellationToken);
                await using (var probe = new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString)))
                {
                    await EnsureNotNewerThanAppAsync(fullPath, probe, cancellationToken);
                }

                await CloseCoreAsync(cancellationToken);

                Workspace workspace;
                await using (var context = new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString)))
                {
                    await context.Database.MigrateAsync(cancellationToken);

                    workspace = await context.Workspace.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
                        ?? throw new WorkspaceFileException(WorkspaceFileError.Corrupted, fullPath, $"'{fullPath}' has no workspace metadata.");
                }

                return SetCurrent(fullPath, connectionString, workspace);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteNotADatabase or SqliteCorrupt)
            {
                SqliteConnection.ClearPool(new SqliteConnection(connectionString));
                throw new WorkspaceFileException(WorkspaceFileError.NotAWorkspace, fullPath, $"'{fullPath}' is not an {WorkspaceFile.DisplayName} file.", ex);
            }
            catch (Exception ex)
            {
                SqliteConnection.ClearPool(new SqliteConnection(connectionString));
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

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _gate.Dispose();
    }

    /// <summary>
    /// Creates a context over the current workspace. Callers own and dispose it.
    /// </summary>
    internal WorkspaceDbContext CreateDbContext() =>
        _connectionString is { } connectionString
            ? new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString))
            : throw new InvalidOperationException("No workspace is open.");

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

    private WorkspaceInfo SetCurrent(string fullPath, string connectionString, Workspace workspace)
    {
        _connectionString = connectionString;
        Current = new WorkspaceInfo(fullPath, workspace.Id, workspace.Name, workspace.Description, workspace.CreatedAt);
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
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
