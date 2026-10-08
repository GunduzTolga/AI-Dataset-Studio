using System.Globalization;
using System.Text;
using System.Text.Json;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Infrastructure.Analysis;
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

        var sql = new StringBuilder(SummaryColumns);
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

        var items = await ReadSummariesAsync(command, pageSize + 1, cancellationToken);

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

        var sql = new StringBuilder($"SELECT COUNT(*) {From}");
        AppendFilter(sql, command, datasetId, filter, match);
        command.CommandText = sql.Append(';').ToString();
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyDictionary<RecordStatus, long>> CountByStatusAsync(
        Guid datasetId,
        RecordFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var match = SearchText.ToMatchExpression(filter.SearchText);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var sql = new StringBuilder($"SELECT r.Status, COUNT(*) {From}");
        AppendFilter(sql, command, datasetId, filter with { Statuses = null }, match);
        command.CommandText = sql.Append(" GROUP BY r.Status;").ToString();

        var counts = Enum.GetValues<RecordStatus>().ToDictionary(status => status, _ => 0L);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[(RecordStatus)reader.GetInt32(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    public async Task<IReadOnlyList<RecordSummary>> GetSummariesAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return [];
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SummaryColumns + " WHERE r.Id IN (SELECT value FROM json_each($ids)) ORDER BY r.Ordinal, r.Id;";
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(recordIds.Select(SqliteValues.Guid)));
        return await ReadSummariesAsync(command, recordIds.Count, cancellationToken);
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
                [.. record.Tags.OrderBy(t => t.NameKey, StringComparer.Ordinal).Select(t => t.Id)],
                record.CreatedAt,
                record.UpdatedAt);
    }

    public async Task<IReadOnlyList<RecordSnapshot>> GetSnapshotsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return [];
        }

        var ids = recordIds.Distinct().ToList();
        await using var context = database.CreateDbContext();
        var records = await context.Records.AsNoTracking()
            .Include(r => r.Tags)
            .Where(r => ids.Contains(r.Id))
            .ToListAsync(cancellationToken);

        return records
            .OrderBy(r => r.Ordinal)
            .Select(r => new RecordSnapshot(
                r.Id,
                r.DatasetId,
                r.Ordinal,
                r.Status,
                r.Payload,
                r.Lineage,
                r.MetadataJson,
                r.Tags.Select(t => t.Id).ToList(),
                r.CreatedAt,
                r.UpdatedAt))
            .ToList();
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

        if (AnalysisSql.Condition(filter.Quality) is { } quality)
        {
            sql.Append(" AND ").Append(quality);
        }

        if (filter.TagId is { } tagId)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM RecordTags rt WHERE rt.RecordId = r.Id AND rt.TagId = $tag)");
            command.Parameters.AddWithValue("$tag", SqliteValues.Guid(tagId));
        }
        else if (filter.Untagged)
        {
            sql.Append(" AND NOT EXISTS (SELECT 1 FROM RecordTags rt WHERE rt.RecordId = r.Id)");
        }
    }

    // Lists read the record's up-to-date analysis (if any) for tokens, score and quality filters.
    private static readonly string From = $"FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}";

    // Tags as a JSON array of ids, or NULL without tags.
    private static readonly string SummaryColumns =
        $"SELECT r.Id, r.Ordinal, r.Status, r.Preview, r.UpdatedAt, a.TotalTokens, {AnalysisSql.Score}, " +
        $"COALESCE({AnalysisSql.IsOverLimit}, 0), " +
        $"(SELECT json_group_array(rt.TagId) FROM RecordTags rt WHERE rt.RecordId = r.Id HAVING COUNT(*) > 0) {From}";

    private static List<Guid> ReadIds(string json) => [.. (JsonSerializer.Deserialize<List<string>>(json) ?? []).Select(SqliteValues.ReadGuid)];

    private static async Task<List<RecordSummary>> ReadSummariesAsync(SqliteCommand command, int capacity, CancellationToken cancellationToken)
    {
        var items = new List<RecordSummary>(capacity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RecordSummary(
                SqliteValues.ReadGuid(reader.GetString(0)),
                reader.GetInt64(1),
                (RecordStatus)reader.GetInt32(2),
                reader.GetString(3),
                SqliteValues.ReadTimestamp(reader.GetInt64(4)),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : ReadIds(reader.GetString(8))));
        }

        return items;
    }
}
