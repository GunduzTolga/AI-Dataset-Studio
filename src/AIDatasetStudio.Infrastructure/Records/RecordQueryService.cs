using System.Globalization;
using System.Text;
using System.Text.Json;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Records;

/// <summary>
/// Record lists use raw SQL: keyset pagination over (Ordinal, Id), full-text search through
/// the FTS5 index, and only the lightweight columns a list row needs.
/// </summary>
internal sealed class RecordQueryService(IWorkspaceDatabase database) : IRecordQueryService
{
    public const int MaxPageSize = 1_000;

    public async Task<RecordPage> GetPageAsync(
        Guid datasetId,
        RecordFilter filter,
        RecordCursor? after,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);

        var match = SearchText.ToMatchExpression(filter.SearchText);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var sql = new StringBuilder("SELECT r.Id, r.Ordinal, r.Status, r.Preview, r.UpdatedAt FROM DatasetRecords r");
        AppendFilter(sql, command, datasetId, filter, match);
        if (after is { } cursor)
        {
            sql.Append(" AND (r.Ordinal > $afterOrdinal OR (r.Ordinal = $afterOrdinal AND r.Id > $afterId))");
            command.Parameters.AddWithValue("$afterOrdinal", cursor.Ordinal);
            command.Parameters.AddWithValue("$afterId", SqliteValues.Guid(cursor.Id));
        }

        // One extra row tells whether another page follows.
        sql.Append(" ORDER BY r.Ordinal, r.Id LIMIT $take;");
        command.Parameters.AddWithValue("$take", pageSize + 1);
        command.CommandText = sql.ToString();

        var items = new List<RecordSummary>(pageSize + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RecordSummary(
                    SqliteValues.ReadGuid(reader.GetString(0)),
                    reader.GetInt64(1),
                    (RecordStatus)reader.GetInt32(2),
                    reader.GetString(3),
                    SqliteValues.ReadTimestamp(reader.GetInt64(4))));
            }
        }

        if (items.Count <= pageSize)
        {
            return new RecordPage(items, Next: null);
        }

        items.RemoveAt(items.Count - 1);
        return new RecordPage(items, items[^1].Cursor);
    }

    public async Task<long> CountAsync(Guid datasetId, RecordFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var match = SearchText.ToMatchExpression(filter.SearchText);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var sql = new StringBuilder("SELECT COUNT(*) FROM DatasetRecords r");
        AppendFilter(sql, command, datasetId, filter, match);
        command.CommandText = sql.Append(';').ToString();
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<RecordDetails?> GetAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var record = await context.Records.AsNoTracking()
            .Include(r => r.Tags)
            .SingleOrDefaultAsync(r => r.Id == recordId, cancellationToken);

        return record is null
            ? null
            : new RecordDetails(
                record.Id,
                record.DatasetId,
                record.Ordinal,
                record.Status,
                record.Payload,
                record.ContentHash,
                record.Lineage,
                record.Tags.Select(t => t.Name).Order(StringComparer.Ordinal).ToList(),
                record.CreatedAt,
                record.UpdatedAt);
    }

    private static void AppendFilter(StringBuilder sql, SqliteCommand command, Guid datasetId, RecordFilter filter, string? match)
    {
        sql.Append(" WHERE r.DatasetId = $datasetId");
        command.Parameters.AddWithValue("$datasetId", SqliteValues.Guid(datasetId));

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            sql.Append(" AND r.Status IN (SELECT value FROM json_each($statuses))");
            command.Parameters.AddWithValue("$statuses", JsonSerializer.Serialize(statuses.Select(s => (int)s).Order()));
        }

        if (match is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" AND r.Id IN ({SearchIndex.MatchingRecordIdsSql})");
            command.Parameters.AddWithValue("$match", match);
        }
    }
}
