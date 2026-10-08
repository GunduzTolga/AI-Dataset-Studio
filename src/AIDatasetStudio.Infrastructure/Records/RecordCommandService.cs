using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using AIDatasetStudio.Infrastructure.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AIDatasetStudio.Infrastructure.Records;

internal sealed class RecordCommandService(IWorkspaceDatabase database, TimeProvider timeProvider) : IRecordCommandService
{
    private const int ProgressInterval = 1_000;

    public async Task<Guid> AddAsync(Guid datasetId, RecordPayload payload, RecordLineage lineage, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var dataset = await context.Datasets.AsNoTracking().SingleOrDefaultAsync(d => d.Id == datasetId, cancellationToken)
            ?? throw new NotFoundException(nameof(Dataset), datasetId);
        var ordinal = await NextOrdinalAsync(context, datasetId, cancellationToken);
        var record = DatasetRecord.Create(dataset, payload, lineage, ordinal, timeProvider.GetUtcNow());

        context.Records.Add(record);
        await context.SaveChangesAsync(cancellationToken);
        await IndexAsync(context, transaction, record.Id, payload, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record.Id;
    }

    public async Task<int> AddRangeAsync(
        Guid datasetId,
        IEnumerable<NewRecord> records,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        Dataset dataset;
        await using (var context = database.CreateDbContext())
        {
            dataset = await context.Datasets.AsNoTracking().SingleOrDefaultAsync(d => d.Id == datasetId, cancellationToken)
                ?? throw new NotFoundException(nameof(Dataset), datasetId);
        }

        // EF Core's change tracker is far too slow for 100k rows; use prepared statements in one transaction.
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var ordinal = await ScalarAsync(connection, transaction, "SELECT IFNULL(MAX(Ordinal), 0) FROM DatasetRecords WHERE DatasetId = $ds;", SqliteValues.Guid(datasetId), cancellationToken);
        var now = timeProvider.GetUtcNow();
        var count = 0;

        await using (var insert = CreateInsertCommand(connection, transaction))
        await using (var index = new SearchIndex.Writer(connection, transaction))
        {
            foreach (var item in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = DatasetRecord.Create(dataset, item.Payload, item.Lineage, ++ordinal, now);
                BindInsert(insert, record);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                await index.UpsertAsync(record.Id, SearchText.ForPayload(record.Payload), cancellationToken);

                if (++count % ProgressInterval == 0)
                {
                    progress?.Report(count);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        progress?.Report(count);
        return count;
    }

    public async Task<bool> UpdatePayloadAsync(Guid recordId, RecordPayload payload, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var record = await context.Records.SingleOrDefaultAsync(r => r.Id == recordId, cancellationToken)
            ?? throw new NotFoundException(nameof(DatasetRecord), recordId);
        if (!record.UpdatePayload(payload, timeProvider.GetUtcNow()))
        {
            return false;
        }

        await context.SaveChangesAsync(cancellationToken);
        await IndexAsync(context, transaction, record.Id, payload, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<StatusChangeResult> SetStatusAsync(IReadOnlyCollection<Guid> recordIds, RecordStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return StatusChangeResult.None;
        }

        await using var context = database.CreateDbContext();
        var ids = recordIds.Distinct().ToList();
        var records = await context.Records.Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var changes = new List<RecordStatusUpdate>();
        foreach (var record in records)
        {
            if (record.Status != status && RecordStatusTransitions.CanTransition(record.Status, status))
            {
                changes.Add(new RecordStatusUpdate(record.Id, record.Status, status));
                record.ChangeStatus(status, now);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return new StatusChangeResult(changes, ids.Count - changes.Count);
    }

    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return 0;
        }

        await using var context = database.CreateDbContext();
        var ids = recordIds.Distinct().ToList();
        // A trigger removes the deleted records from the search index.
        return await context.Records.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<int> RestoreStatesAsync(IReadOnlyCollection<RecordState> states, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count == 0)
        {
            return 0;
        }

        // The last state given for a record wins.
        var byId = new Dictionary<Guid, RecordState>();
        foreach (var state in states)
        {
            byId[state.RecordId] = state;
        }

        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ids = byId.Keys.ToList();
        var records = await context.Records.Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var reindex = records.Where(record => record.RestoreState(byId[record.Id].Status, byId[record.Id].Payload, now)).ToList();

        await context.SaveChangesAsync(cancellationToken);
        if (reindex.Count > 0)
        {
            await using var index = Writer(context, transaction);
            foreach (var record in reindex)
            {
                await index.UpsertAsync(record.Id, SearchText.ForPayload(record.Payload), cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return records.Count;
    }

    public async Task<int> RestoreAsync(IReadOnlyCollection<RecordSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return 0;
        }

        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var datasetIds = snapshots.Select(s => s.DatasetId).Distinct().ToList();
        var datasets = await context.Datasets.Where(d => datasetIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
        if (datasetIds.FirstOrDefault(id => !datasets.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            throw new NotFoundException(nameof(Dataset), missing);
        }

        var ids = snapshots.Select(s => s.Id).ToList();
        var existing = (await context.Records.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken)).ToHashSet();
        var tagIds = snapshots.SelectMany(s => s.TagIds).Distinct().ToList();
        var tags = await context.Tags.Where(t => tagIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);

        var restored = new List<DatasetRecord>();
        foreach (var snapshot in snapshots.Where(s => existing.Add(s.Id)))
        {
            var record = DatasetRecord.Recreate(
                datasets[snapshot.DatasetId],
                snapshot.Id,
                snapshot.Ordinal,
                snapshot.Status,
                snapshot.Payload,
                snapshot.Lineage,
                snapshot.MetadataJson,
                snapshot.CreatedAt,
                snapshot.UpdatedAt);
            foreach (var tagId in snapshot.TagIds)
            {
                if (tags.TryGetValue(tagId, out var tag))
                {
                    record.AddTag(tag, snapshot.UpdatedAt);
                }
            }

            restored.Add(record);
        }

        context.Records.AddRange(restored);
        await context.SaveChangesAsync(cancellationToken);
        await using (var index = Writer(context, transaction))
        {
            foreach (var record in restored)
            {
                await index.UpsertAsync(record.Id, SearchText.ForPayload(record.Payload), cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return restored.Count;
    }

    private static async Task<long> NextOrdinalAsync(WorkspaceDbContext context, Guid datasetId, CancellationToken cancellationToken) =>
        (await context.Records.Where(r => r.DatasetId == datasetId).MaxAsync(r => (long?)r.Ordinal, cancellationToken) ?? 0) + 1;

    private static async Task IndexAsync(
        WorkspaceDbContext context,
        IDbContextTransaction transaction,
        Guid recordId,
        RecordPayload payload,
        CancellationToken cancellationToken)
    {
        await using var index = Writer(context, transaction);
        await index.UpsertAsync(recordId, SearchText.ForPayload(payload), cancellationToken);
    }

    private static SearchIndex.Writer Writer(WorkspaceDbContext context, IDbContextTransaction transaction) =>
        new((SqliteConnection)context.Database.GetDbConnection(), (SqliteTransaction)transaction.GetDbTransaction());

    private static async Task<long> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, string datasetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ds", datasetId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // Column values must match what EF Core writes (see SqliteValues and the entity configuration).
    private static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO DatasetRecords (
                Id, DatasetId, Type, Status, Ordinal, DataPayload, ContentHash, Preview, MetadataJson,
                SourceKind, SourceReference, CreatedBy, GeneratedByModel, CreatedAt, UpdatedAt)
            VALUES (
                $id, $datasetId, $type, $status, $ordinal, $payload, $hash, $preview, $metadata,
                $sourceKind, $sourceReference, $createdBy, $generatedByModel, $createdAt, $updatedAt);
            """;
        foreach (var name in new[]
        {
            "$id", "$datasetId", "$type", "$status", "$ordinal", "$payload", "$hash", "$preview", "$metadata",
            "$sourceKind", "$sourceReference", "$createdBy", "$generatedByModel", "$createdAt", "$updatedAt",
        })
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        command.Prepare();
        return command;
    }

    private static void BindInsert(SqliteCommand command, DatasetRecord record)
    {
        var p = command.Parameters;
        p["$id"].Value = SqliteValues.Guid(record.Id);
        p["$datasetId"].Value = SqliteValues.Guid(record.DatasetId);
        p["$type"].Value = (int)record.Type;
        p["$status"].Value = (int)record.Status;
        p["$ordinal"].Value = record.Ordinal;
        p["$payload"].Value = PayloadJson.Serialize(record.Payload);
        p["$hash"].Value = record.ContentHash;
        p["$preview"].Value = record.Preview;
        p["$metadata"].Value = record.MetadataJson;
        p["$sourceKind"].Value = (int)record.Lineage.SourceKind;
        p["$sourceReference"].Value = SqliteValues.NullableText(record.Lineage.SourceReference);
        p["$createdBy"].Value = SqliteValues.NullableText(record.Lineage.CreatedBy);
        p["$generatedByModel"].Value = SqliteValues.NullableText(record.Lineage.GeneratedByModel);
        p["$createdAt"].Value = SqliteValues.Timestamp(record.CreatedAt);
        p["$updatedAt"].Value = SqliteValues.Timestamp(record.UpdatedAt);
    }
}
