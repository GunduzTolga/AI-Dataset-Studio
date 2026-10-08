using System.Text.Json;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Search;

/// <summary>
/// Maintains the full-text index of record text.
/// </summary>
/// <remarks>
/// <para>
/// <c>RecordSearch</c> is a contentless FTS5 table: it stores only the index, not a second copy of
/// the text. Its rowids come from <c>RecordSearchKeys.RowId</c>, an INTEGER PRIMARY KEY, because
/// SQLite may renumber the implicit rowid of <c>DatasetRecords</c> during VACUUM.
/// </para>
/// <para>
/// Writes go through <see cref="UpsertAsync"/>; deletes (including cascades from datasets and
/// projects) are handled by a trigger created in the AddRecordSearch migration.
/// </para>
/// </remarks>
internal static class SearchIndex
{
    /// <summary>
    /// Prepared statements for indexing many records in one transaction.
    /// </summary>
    public sealed class Writer : IAsyncDisposable
    {
        private readonly SqliteCommand _ensureKey;
        private readonly SqliteCommand _remove;
        private readonly SqliteCommand _insert;

        public Writer(SqliteConnection connection, SqliteTransaction transaction)
        {
            _ensureKey = Create(connection, transaction, """
                INSERT INTO RecordSearchKeys (RecordId) VALUES ($id)
                ON CONFLICT (RecordId) DO UPDATE SET RecordId = excluded.RecordId
                RETURNING RowId;
                """, "$id");
            _remove = Create(connection, transaction, "DELETE FROM RecordSearch WHERE rowid = $rowid;", "$rowid");
            _insert = Create(connection, transaction, "INSERT INTO RecordSearch (rowid, Text) VALUES ($rowid, $text);", "$rowid", "$text");
        }

        public async Task UpsertAsync(Guid recordId, string normalizedText, CancellationToken cancellationToken)
        {
            _ensureKey.Parameters["$id"].Value = SqliteValues.Guid(recordId);
            var rowId = (long)(await _ensureKey.ExecuteScalarAsync(cancellationToken))!;

            _remove.Parameters["$rowid"].Value = rowId;
            await _remove.ExecuteNonQueryAsync(cancellationToken);

            _insert.Parameters["$rowid"].Value = rowId;
            _insert.Parameters["$text"].Value = normalizedText;
            await _insert.ExecuteNonQueryAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _ensureKey.DisposeAsync();
            await _remove.DisposeAsync();
            await _insert.DisposeAsync();
        }

        private static SqliteCommand Create(SqliteConnection connection, SqliteTransaction transaction, string sql, params string[] parameters)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var name in parameters)
            {
                command.Parameters.Add(new SqliteParameter { ParameterName = name });
            }

            command.Prepare();
            return command;
        }
    }

    /// <summary>
    /// SQL fragment selecting the ids of records whose text matches the <c>$match</c> parameter.
    /// </summary>
    public const string MatchingRecordIdsSql = """
        SELECT k.RecordId FROM RecordSearch s
        JOIN RecordSearchKeys k ON k.RowId = s.rowid
        WHERE RecordSearch MATCH $match
        """;

    /// <summary>
    /// Records repaired per transaction, so a large upgrade never holds every payload in memory.
    /// </summary>
    public const int RepairBatchSize = 1000;

    /// <summary>
    /// Fills in derived data (search index, list preview) for records that lack it: records
    /// written by an older version, or by a crash between the record write and the index write.
    /// </summary>
    /// <returns>The number of records repaired.</returns>
    public static async Task<int> RepairAsync(SqliteConnection connection, CancellationToken cancellationToken, int batchSize = RepairBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var repaired = 0;
        // Every record of a batch gets a key (even an unreadable one), so the next query moves on.
        while (await ReadPendingAsync(connection, batchSize, cancellationToken) is { Count: > 0 } pending)
        {
            await RepairBatchAsync(connection, pending, cancellationToken);
            repaired += pending.Count;
        }

        return repaired;
    }

    private static async Task<List<(string Id, string Json)>> ReadPendingAsync(SqliteConnection connection, int batchSize, CancellationToken cancellationToken)
    {
        var pending = new List<(string Id, string Json)>();
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT r.Id, r.DataPayload FROM DatasetRecords r
            LEFT JOIN RecordSearchKeys k ON k.RecordId = r.Id
            WHERE k.RecordId IS NULL
            LIMIT $limit;
            """;
        select.Parameters.AddWithValue("$limit", batchSize);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add((reader.GetString(0), reader.GetString(1)));
        }

        return pending;
    }

    private static async Task RepairBatchAsync(SqliteConnection connection, List<(string Id, string Json)> pending, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var writer = new Writer(connection, transaction))
        await using (var setPreview = connection.CreateCommand())
        {
            setPreview.Transaction = transaction;
            setPreview.CommandText = "UPDATE DatasetRecords SET Preview = $preview WHERE Id = $id AND Preview = '';";
            var previewParameter = setPreview.Parameters.Add(new SqliteParameter { ParameterName = "$preview" });
            var idParameter = setPreview.Parameters.Add(new SqliteParameter { ParameterName = "$id" });

            foreach (var (id, json) in pending)
            {
                string text;
                string preview;
                try
                {
                    var payload = PayloadJson.Deserialize(json);
                    text = SearchText.ForPayload(payload);
                    preview = payload.GetPreviewText(Domain.Entities.DatasetRecord.PreviewMaxLength);
                }
                catch (JsonException)
                {
                    // An unreadable payload must not block opening the workspace; index it as empty
                    // so it is not retried on every open. The health engine reports such records.
                    text = string.Empty;
                    preview = string.Empty;
                }

                await writer.UpsertAsync(SqliteValues.ReadGuid(id), text, cancellationToken);
                if (preview.Length > 0)
                {
                    previewParameter.Value = preview;
                    idParameter.Value = id;
                    await setPreview.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
