using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Persistence;

/// <summary>
/// Access to the database of the currently open workspace.
/// </summary>
internal interface IWorkspaceDatabase
{
    /// <exception cref="InvalidOperationException">No workspace is open.</exception>
    WorkspaceDbContext CreateDbContext();

    /// <summary>
    /// Opens a raw connection for bulk writes and list queries that EF Core would make slow.
    /// </summary>
    /// <exception cref="InvalidOperationException">No workspace is open.</exception>
    Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
