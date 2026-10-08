using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Persistence;

/// <summary>
/// Low-level SQLite settings shared by everything that touches a workspace file.
/// </summary>
internal static class WorkspaceDatabase
{
    /// <summary>
    /// Stored in the SQLite header (<c>PRAGMA application_id</c>) so workspace files can be told
    /// apart from arbitrary SQLite databases. ASCII "ADST".
    /// </summary>
    public const int ApplicationId = 0x41445354;

    public static string BuildConnectionString(string filePath, bool allowCreate) => new SqliteConnectionStringBuilder
    {
        DataSource = filePath,
        Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
        ForeignKeys = true,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    public static DbContextOptions<WorkspaceDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlite(connectionString)
            .Options;

    public static async Task<long> ExecuteScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
