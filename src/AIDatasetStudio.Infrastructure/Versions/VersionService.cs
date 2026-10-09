using System.Text.Json;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Analysis;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using AIDatasetStudio.Infrastructure.Records;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AIDatasetStudio.Infrastructure.Versions;

/// <summary>
/// Versions are written and compared with set-based SQL: a version of 100k records adds 100k
/// small mapping rows, and a content is stored once however many versions keep it
/// (<c>RecordRevisions</c> is unique per record and content hash).
/// </summary>
internal sealed class VersionService(IWorkspaceDatabase database, IRecordAnalysisService analysis, TimeProvider timeProvider) : IVersionService
{
    private const int LookupBatchSize = 1_000;

    public async Task<IReadOnlyList<VersionInfo>> ListAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var versions = await context.Versions.AsNoTracking()
            .Where(v => v.DatasetId == datasetId)
            .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
            .ToListAsync(cancellationToken);
        return [.. versions.Select(ToInfo)];
    }

    public async Task<VersionInfo> CreateAsync(
        Guid datasetId,
        string number,
        string? description,
        IProgress<int>? analysisProgress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = VersionNumber.Parse(number);

        // The version keeps each record's token count with the dataset's tokenizer, and its group of
        // near-duplicate questions so the version is split as the records were (the pass is quick
        // when nothing changed).
        await analysis.AnalyzeAsync(datasetId, analysisProgress, cancellationToken);

        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var dataset = await context.Datasets.AsNoTracking().SingleOrDefaultAsync(d => d.Id == datasetId, cancellationToken)
            ?? throw new NotFoundException(nameof(Dataset), datasetId);
        var text = parsed.ToString();
        if (await context.Versions.AnyAsync(v => v.DatasetId == datasetId && v.Number == text, cancellationToken))
        {
            throw new DomainException($"Version {text} already exists.");
        }

        var (connection, sqlTransaction) = Raw(context, transaction);
        var now = timeProvider.GetUtcNow();

        // Contents not kept yet; a record unchanged since an earlier version reuses its revision.
        await using (var command = Command(connection, sqlTransaction, """
            INSERT INTO RecordRevisions (DatasetId, RecordId, ContentHash, DataPayload, Preview, SourceKind, SourceReference,
                                         CreatedBy, GeneratedByModel, MetadataJson, RecordCreatedAt, CreatedAt)
            SELECT r.DatasetId, r.Id, r.ContentHash, r.DataPayload, r.Preview, r.SourceKind, r.SourceReference,
                   r.CreatedBy, r.GeneratedByModel, r.MetadataJson, r.CreatedAt, $now
            FROM DatasetRecords r
            WHERE r.DatasetId = $ds
              AND NOT EXISTS (SELECT 1 FROM RecordRevisions x WHERE x.RecordId = r.Id AND x.ContentHash = r.ContentHash);
            """))
        {
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
            command.Parameters.AddWithValue("$now", SqliteValues.Timestamp(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long total, approved;
        await using (var command = Command(connection, sqlTransaction,
            $"SELECT COUNT(*), COALESCE(SUM(Status = {(int)RecordStatus.Approved}), 0) FROM DatasetRecords WHERE DatasetId = $ds;"))
        {
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (total, approved) = (reader.GetInt64(0), reader.GetInt64(1));
        }

        var version = DatasetVersion.Create(dataset, parsed, description, total, approved, now);
        context.Versions.Add(version);
        await context.SaveChangesAsync(cancellationToken);

        await using (var command = Command(connection, sqlTransaction, $"""
            INSERT INTO VersionRecordMappings (VersionId, Ordinal, RevisionId, Status, Tokens, TagIds, NearGroup)
            SELECT $version, r.Ordinal, x.Id, r.Status, a.TotalTokens,
                   (SELECT json_group_array(t.TagId) FROM RecordTags t WHERE t.RecordId = r.Id HAVING COUNT(*) > 0),
                   a.NearGroup
            FROM DatasetRecords r
            JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
            JOIN RecordRevisions x ON x.RecordId = r.Id AND x.ContentHash = r.ContentHash
            WHERE r.DatasetId = $ds;
            """))
        {
            command.Parameters.AddWithValue("$version", SqliteValues.Guid(version.Id));
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ToInfo(version);
    }

    public async Task DeleteAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var version = await context.Versions.SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw new NotFoundException(nameof(DatasetVersion), versionId);

        // Its mappings go with it (foreign-key cascade); then every content no version keeps.
        context.Versions.Remove(version);
        await context.SaveChangesAsync(cancellationToken);
        var (connection, sqlTransaction) = Raw(context, transaction);
        await using (var command = Command(connection, sqlTransaction, """
            DELETE FROM RecordRevisions
            WHERE DatasetId = $ds AND NOT EXISTS (SELECT 1 FROM VersionRecordMappings m WHERE m.RevisionId = RecordRevisions.Id);
            """))
        {
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(version.DatasetId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<VersionComparison> CompareAsync(Guid datasetId, Guid? olderVersionId, Guid? newerVersionId, CancellationToken cancellationToken = default) =>
        // Up to two full lists of a large dataset: keep the work off the caller's (UI) thread.
        Task.Run(() => CompareCoreAsync(datasetId, olderVersionId, newerVersionId, cancellationToken), cancellationToken);

    public async Task<RecordPayload?> GetContentAsync(Guid datasetId, Guid? versionId, Guid recordId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (versionId is { } version)
        {
            command.CommandText = """
                SELECT x.DataPayload FROM RecordRevisions x
                JOIN VersionRecordMappings m ON m.RevisionId = x.Id
                JOIN DatasetVersions v ON v.Id = m.VersionId
                WHERE x.RecordId = $id AND m.VersionId = $version AND v.DatasetId = $ds;
                """;
            command.Parameters.AddWithValue("$version", SqliteValues.Guid(version));
        }
        else
        {
            command.CommandText = "SELECT DataPayload FROM DatasetRecords WHERE Id = $id AND DatasetId = $ds;";
        }

        command.Parameters.AddWithValue("$id", SqliteValues.Guid(recordId));
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        return await command.ExecuteScalarAsync(cancellationToken) is string json ? PayloadJson.Deserialize(json) : null;
    }

    public async Task<RollbackResult> RollbackAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var version = await context.Versions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            ?? throw new NotFoundException(nameof(DatasetVersion), versionId);
        var (connection, sqlTransaction) = Raw(context, transaction);

        var target = await ReadVersionRowsAsync(connection, sqlTransaction, versionId, cancellationToken);
        var current = await ReadStatesAsync(connection, sqlTransaction, version.DatasetId, null, cancellationToken);

        // What the version needs from each revision: the content where it differs, everything where the record is gone.
        var needed = new List<long>();
        foreach (var (recordId, row) in target)
        {
            if (!current.TryGetValue(recordId, out var now) || now.ContentHash != row.ContentHash)
            {
                needed.Add(row.RevisionId);
            }
        }

        var revisions = await ReadRevisionsAsync(connection, sqlTransaction, needed, cancellationToken);
        var states = new List<RecordState>();
        var snapshots = new List<RecordSnapshot>();
        int contentRestored = 0, statusRestored = 0, archived = 0;
        var time = timeProvider.GetUtcNow();
        foreach (var (recordId, row) in target.OrderBy(t => t.Value.Ordinal))
        {
            if (!current.TryGetValue(recordId, out var now))
            {
                var revision = revisions[row.RevisionId];
                snapshots.Add(new RecordSnapshot(
                    recordId,
                    version.DatasetId,
                    row.Ordinal,
                    row.Status,
                    revision.Payload,
                    revision.Lineage,
                    revision.MetadataJson,
                    row.TagIds,
                    revision.RecordCreatedAt,
                    time));
            }
            else if (now.ContentHash != row.ContentHash)
            {
                states.Add(new RecordState(recordId, row.Status, revisions[row.RevisionId].Payload, TagsIfChanged(now.TagIds, row.TagIds)));
                contentRestored++;
            }
            else if (now.Status != row.Status || !SameTags(now.TagIds, row.TagIds))
            {
                states.Add(new RecordState(recordId, row.Status, TagIds: TagsIfChanged(now.TagIds, row.TagIds)));
                statusRestored++;
            }
        }

        // Records added since are archived, never deleted: going back loses nothing.
        foreach (var (recordId, now) in current.OrderBy(c => c.Value.Ordinal))
        {
            if (!target.ContainsKey(recordId) && now.Status != RecordStatus.Archived)
            {
                states.Add(new RecordState(recordId, RecordStatus.Archived));
                archived++;
            }
        }

        var before = await RecordCommandService.ApplyStatesAsync(context, transaction, states, time, cancellationToken);
        var recreated = await RecordCommandService.RecreateAsync(context, transaction, snapshots, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var recreatedIds = recreated.Select(r => r.Id).ToHashSet();
        return new RollbackResult(
            version.Number,
            before,
            states,
            [.. snapshots.Where(s => recreatedIds.Contains(s.Id))],
            contentRestored,
            statusRestored,
            archived);
    }

    private async Task<VersionComparison> CompareCoreAsync(Guid datasetId, Guid? olderVersionId, Guid? newerVersionId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        // One read snapshot for both sides; it does not block edits (WAL).
        await using var snapshot = connection.BeginTransaction(deferred: true);
        await EnsureExistsAsync(connection, snapshot, datasetId, olderVersionId, cancellationToken);
        await EnsureExistsAsync(connection, snapshot, datasetId, newerVersionId, cancellationToken);
        var older = await ReadStatesAsync(connection, snapshot, datasetId, olderVersionId, cancellationToken);
        var newer = await ReadStatesAsync(connection, snapshot, datasetId, newerVersionId, cancellationToken);

        var differences = new List<(Guid Id, DifferenceKind Kind, long Ordinal, StateRow? Old, StateRow? New)>();
        long unchanged = 0;
        foreach (var (recordId, now) in newer)
        {
            if (!older.TryGetValue(recordId, out var then))
            {
                differences.Add((recordId, DifferenceKind.Added, now.Ordinal, null, now));
            }
            else if (then.ContentHash != now.ContentHash)
            {
                differences.Add((recordId, DifferenceKind.Modified, now.Ordinal, then, now));
            }
            else if (then.Status != now.Status)
            {
                differences.Add((recordId, DifferenceKind.StatusChanged, now.Ordinal, then, now));
            }
            else if (!SameTags(then.TagIds, now.TagIds))
            {
                differences.Add((recordId, DifferenceKind.TagsChanged, now.Ordinal, then, now));
            }
            else
            {
                unchanged++;
            }
        }

        foreach (var (recordId, then) in older)
        {
            if (!newer.ContainsKey(recordId))
            {
                differences.Add((recordId, DifferenceKind.Removed, then.Ordinal, then, null));
            }
        }

        // Previews only for the records that differ: a removed record shows its older text.
        var newerPreviews = await ReadPreviewsAsync(connection, snapshot, datasetId, newerVersionId,
            [.. differences.Where(d => d.Kind != DifferenceKind.Removed).Select(d => d.Id)], cancellationToken);
        var olderPreviews = await ReadPreviewsAsync(connection, snapshot, datasetId, olderVersionId,
            [.. differences.Where(d => d.Kind == DifferenceKind.Removed).Select(d => d.Id)], cancellationToken);

        return new VersionComparison(
            [.. differences
                .OrderBy(d => d.Ordinal).ThenBy(d => d.Kind)
                .Select(d => new RecordDifference(
                    d.Id,
                    d.Kind,
                    d.Ordinal,
                    (d.Kind == DifferenceKind.Removed ? olderPreviews : newerPreviews).GetValueOrDefault(d.Id, string.Empty),
                    d.Old?.Status,
                    d.New?.Status,
                    d.Old?.TagIds,
                    d.New?.TagIds))],
            unchanged);
    }

    private static async Task EnsureExistsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid datasetId, Guid? versionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, versionId is null
            ? "SELECT COUNT(*) FROM Datasets WHERE Id = $ds;"
            : "SELECT COUNT(*) FROM DatasetVersions WHERE Id = $version AND DatasetId = $ds;");
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        if (versionId is { } version)
        {
            command.Parameters.AddWithValue("$version", SqliteValues.Guid(version));
        }

        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0)
        {
            throw versionId is { } id ? new NotFoundException(nameof(DatasetVersion), id) : new NotFoundException(nameof(Dataset), datasetId);
        }
    }

    // Every record of the working copy or a version: position, status, content hash and tags.
    private static async Task<Dictionary<Guid, StateRow>> ReadStatesAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid datasetId, Guid? versionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, versionId is null
            ? $"SELECT r.Id, r.Ordinal, r.Status, r.ContentHash, {CurrentTagsSql} FROM DatasetRecords r WHERE r.DatasetId = $ds;"
            : $"""
              SELECT x.RecordId, m.Ordinal, m.Status, x.ContentHash, {VersionTagsSql}
              FROM VersionRecordMappings m JOIN RecordRevisions x ON x.Id = m.RevisionId
              WHERE m.VersionId = $version;
              """);
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        if (versionId is { } version)
        {
            command.Parameters.AddWithValue("$version", SqliteValues.Guid(version));
        }

        var rows = new Dictionary<Guid, StateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[SqliteValues.ReadGuid(reader.GetString(0))] =
                new StateRow(reader.GetInt64(1), (RecordStatus)reader.GetInt32(2), reader.GetString(3), ReadTags(reader, 4));
        }

        return rows;
    }

    private static async Task<Dictionary<Guid, VersionRow>> ReadVersionRowsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid versionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"""
            SELECT x.RecordId, m.Ordinal, m.Status, x.ContentHash, x.Id, {VersionTagsSql}
            FROM VersionRecordMappings m JOIN RecordRevisions x ON x.Id = m.RevisionId
            WHERE m.VersionId = $version;
            """);
        command.Parameters.AddWithValue("$version", SqliteValues.Guid(versionId));

        var rows = new Dictionary<Guid, VersionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[SqliteValues.ReadGuid(reader.GetString(0))] =
                new VersionRow(reader.GetInt64(1), (RecordStatus)reader.GetInt32(2), reader.GetString(3), reader.GetInt64(4), ReadTags(reader, 5));
        }

        return rows;
    }

    private static async Task<Dictionary<long, RevisionRow>> ReadRevisionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<long> revisionIds, CancellationToken cancellationToken)
    {
        var rows = new Dictionary<long, RevisionRow>(revisionIds.Count);
        foreach (var batch in revisionIds.Chunk(LookupBatchSize))
        {
            await using var command = Command(connection, transaction, """
                SELECT Id, DataPayload, SourceKind, SourceReference, CreatedBy, GeneratedByModel, MetadataJson, RecordCreatedAt
                FROM RecordRevisions WHERE Id IN (SELECT value FROM json_each($ids));
                """);
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(batch));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows[reader.GetInt64(0)] = new RevisionRow(
                    PayloadJson.Deserialize(reader.GetString(1)),
                    new RecordLineage(
                        (RecordSourceKind)reader.GetInt32(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5)),
                    reader.GetString(6),
                    SqliteValues.ReadTimestamp(reader.GetInt64(7)));
            }
        }

        return rows;
    }

    private static async Task<Dictionary<Guid, string>> ReadPreviewsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid datasetId, Guid? versionId, IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken)
    {
        var previews = new Dictionary<Guid, string>(recordIds.Count);
        foreach (var batch in recordIds.Chunk(LookupBatchSize))
        {
            await using var command = Command(connection, transaction, versionId is null
                ? "SELECT Id, Preview FROM DatasetRecords WHERE DatasetId = $ds AND Id IN (SELECT value FROM json_each($ids));"
                : """
                  SELECT x.RecordId, x.Preview
                  FROM RecordRevisions x JOIN VersionRecordMappings m ON m.RevisionId = x.Id
                  WHERE m.VersionId = $version AND x.RecordId IN (SELECT value FROM json_each($ids));
                  """);
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(batch.Select(SqliteValues.Guid)));
            if (versionId is { } version)
            {
                command.Parameters.AddWithValue("$version", SqliteValues.Guid(version));
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                previews[SqliteValues.ReadGuid(reader.GetString(0))] = reader.GetString(1);
            }
        }

        return previews;
    }

    // Tags as a JSON array of ids or NULL. A version's tags that were deleted since no longer count.
    private const string CurrentTagsSql = "(SELECT json_group_array(rt.TagId) FROM RecordTags rt WHERE rt.RecordId = r.Id HAVING COUNT(*) > 0)";

    private const string VersionTagsSql =
        "(SELECT json_group_array(j.value) FROM json_each(m.TagIds) j WHERE j.value IN (SELECT Id FROM Tags) HAVING COUNT(*) > 0)";

    private static IReadOnlyList<Guid> ReadTags(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? []
            : [.. (JsonSerializer.Deserialize<List<string>>(reader.GetString(ordinal)) ?? []).Select(SqliteValues.ReadGuid).Order()];

    private static bool SameTags(IReadOnlyList<Guid> a, IReadOnlyList<Guid> b) => a.SequenceEqual(b);

    private static IReadOnlyList<Guid>? TagsIfChanged(IReadOnlyList<Guid> current, IReadOnlyList<Guid> target) =>
        SameTags(current, target) ? null : target;

    private static (SqliteConnection Connection, SqliteTransaction Transaction) Raw(WorkspaceDbContext context, IDbContextTransaction transaction) =>
        ((SqliteConnection)context.Database.GetDbConnection(), (SqliteTransaction)transaction.GetDbTransaction());

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static VersionInfo ToInfo(DatasetVersion v) =>
        new(v.Id, v.DatasetId, v.Number, v.Description, v.Tokenizer, v.ContextLength, v.RecordCount, v.ApprovedCount, v.CreatedAt);

    /// <param name="TagIds">Sorted.</param>
    private sealed record StateRow(long Ordinal, RecordStatus Status, string ContentHash, IReadOnlyList<Guid> TagIds);

    private sealed record VersionRow(long Ordinal, RecordStatus Status, string ContentHash, long RevisionId, IReadOnlyList<Guid> TagIds);

    private sealed record RevisionRow(RecordPayload Payload, RecordLineage Lineage, string MetadataJson, DateTimeOffset RecordCreatedAt);
}
