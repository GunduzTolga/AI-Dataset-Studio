using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Analysis;

/// <summary>
/// Computes and stores token counts and single-record rule results in batches; cross-record
/// rules and the context-length check are evaluated in SQL when read (see <see cref="AnalysisSql"/>).
/// Near-duplicate questions need the whole dataset: after analysis a grouping pass stores each
/// record's group (<see cref="UpdateNearGroupsAsync"/>), and SQL checks status and freshness when read.
/// </summary>
internal sealed class RecordAnalysisService(IWorkspaceDatabase database, ITokenizerProvider tokenizers, TimeProvider timeProvider)
    : IRecordAnalysisService
{
    private const int BatchSize = 500;
    private const string CustomTermsKey = "pii.customTerms";

    private static readonly JsonSerializerOptions IssueJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<long> CountPendingAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
            WHERE r.DatasetId = $ds AND a.RecordId IS NULL;
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public Task<int> AnalyzeAsync(Guid datasetId, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        // Tokenizing is CPU work; keep it off the caller's (usually the UI) thread.
        Task.Run(() => AnalyzeDatasetAsync(datasetId, progress, cancellationToken), cancellationToken);

    public Task AnalyzeRecordsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        return recordIds.Count == 0
            ? Task.CompletedTask
            : Task.Run(() => AnalyzeRecordsCoreAsync(recordIds, cancellationToken), cancellationToken);
    }

    public Task<bool> GroupNearDuplicatesAsync(Guid datasetId, CancellationToken cancellationToken = default) =>
        // Comparing questions is CPU work too.
        Task.Run(
            async () =>
            {
                await using var connection = await database.OpenConnectionAsync(cancellationToken);
                return await UpdateNearGroupsAsync(connection, datasetId, cancellationToken);
            },
            cancellationToken);

    public async Task<DatasetQualitySummary> GetSummaryAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var bounds = Tokenizers.HistogramBounds;
        var buckets = new StringBuilder();
        for (var i = 0; i <= bounds.Count; i++)
        {
            var lower = i == 0 ? null : $"TotalTokens >= {bounds[i - 1]}";
            var upper = i == bounds.Count ? null : $"TotalTokens < {bounds[i]}";
            buckets.Append(", COALESCE(SUM(").Append(string.Join(" AND ", new[] { lower, upper }.OfType<string>())).Append("), 0)");
        }

        var ruleSums = string.Concat(QualityRules.RecordRules.Select(rule => $", COALESCE(SUM((IssueFlags & {AnalysisSql.Bit(rule)}) <> 0), 0)"));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH scored AS MATERIALIZED (
                SELECT r.Status, a.TotalTokens, a.IssueFlags, a.Score,
                       {AnalysisSql.IsDuplicate} AS IsDup,
                       {AnalysisSql.IsConflicting} AS IsConflict,
                       {AnalysisSql.IsNearDuplicate} AS IsNear,
                       {AnalysisSql.IsOverLimit} AS IsOver
                FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
                WHERE r.DatasetId = $ds AND a.RecordId IS NOT NULL AND r.Status <> {AnalysisSql.Archived})
            SELECT (SELECT COUNT(*) FROM DatasetRecords WHERE DatasetId = $ds),
                   (SELECT COUNT(*) FROM Datasets WHERE Id = $ds),
                   (SELECT COUNT(*) FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
                    WHERE r.DatasetId = $ds AND a.RecordId IS NOT NULL),
                   -- The score of AnalysisSql.Score, from the flags already computed.
                   AVG(MAX(0, Score - (IsDup + IsConflict + IsNear) * {QualityRules.WarningPenalty} - IsOver * {QualityRules.ErrorPenalty})),
                   COALESCE(SUM(TotalTokens), 0), COALESCE(MAX(TotalTokens), 0), COALESCE(SUM(IsOver), 0),
                   COALESCE(SUM(IsDup), 0), COALESCE(SUM(IsConflict), 0), COALESCE(SUM(IsNear), 0){ruleSums}{buckets}
            FROM scored;
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetInt64(1) == 0)
        {
            throw new NotFoundException(nameof(Dataset), datasetId);
        }

        var issues = new Dictionary<QualityRule, long>();
        void AddCount(QualityRule rule, long count)
        {
            if (count > 0)
            {
                issues[rule] = count;
            }
        }

        var column = 7;
        AddCount(QualityRule.Duplicate, reader.GetInt64(column++));
        AddCount(QualityRule.ConflictingAnswer, reader.GetInt64(column++));
        AddCount(QualityRule.NearDuplicate, reader.GetInt64(column++));
        foreach (var rule in QualityRules.RecordRules)
        {
            AddCount(rule, reader.GetInt64(column++));
        }

        AddCount(QualityRule.OverContextLimit, reader.GetInt64(6));

        var histogram = new List<TokenBucket>();
        for (var i = 0; i <= bounds.Count; i++)
        {
            histogram.Add(new TokenBucket(i == bounds.Count ? int.MaxValue : bounds[i], reader.GetInt64(column++)));
        }

        return new DatasetQualitySummary(
            RecordCount: reader.GetInt64(0),
            AnalyzedCount: reader.GetInt64(2),
            Score: reader.IsDBNull(3) ? null : (int)Math.Round(reader.GetDouble(3), MidpointRounding.AwayFromZero),
            TotalTokens: reader.GetInt64(4),
            MaxTokens: (int)reader.GetInt64(5),
            OverLimitCount: reader.GetInt64(6),
            Histogram: histogram,
            IssueCounts: issues);
    }

    public async Task<RecordQuality?> GetRecordQualityAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var connection = await database.OpenConnectionAsync(cancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT a.RecordId IS NOT NULL, a.InputTokens, a.OutputTokens, a.Issues,
                           {AnalysisSql.IsDuplicate}, {AnalysisSql.IsConflicting}, {AnalysisSql.IsOverLimit}, {AnalysisSql.Score},
                           {AnalysisSql.NearDuplicateOf}
                    FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
                    WHERE r.Id = $id;
                    """;
                command.Parameters.AddWithValue("$id", SqliteValues.Guid(recordId));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return null;
                }

                if (reader.GetBoolean(0))
                {
                    var issues = ReadIssues(reader.GetString(3));
                    if (reader.GetBoolean(4))
                    {
                        issues.Add(Issue(QualityRule.Duplicate));
                    }

                    if (!reader.IsDBNull(5) && reader.GetBoolean(5))
                    {
                        issues.Add(Issue(QualityRule.ConflictingAnswer));
                    }

                    if (!reader.IsDBNull(8))
                    {
                        issues.Add(Issue(QualityRule.NearDuplicate, reader.GetInt64(8).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }

                    if (reader.GetBoolean(6))
                    {
                        issues.Add(Issue(QualityRule.OverContextLimit));
                    }

                    return new RecordQuality(new TokenCounts(reader.GetInt32(1), reader.GetInt32(2)), reader.GetInt32(7), issues);
                }
            }

            // Not analysed yet (new or edited): analyse it now and read again.
            await AnalyzeRecordsCoreAsync([recordId], cancellationToken);
        }

        return new RecordQuality(null, null, []);
    }

    public async Task<IReadOnlyList<Guid>> FindAsync(Guid datasetId, QualityRule rule, CancellationToken cancellationToken = default)
    {
        var condition = rule switch
        {
            // Keep the first record of each group of copies.
            QualityRule.Duplicate =>
                "EXISTS (SELECT 1 FROM DatasetRecords o WHERE o.DatasetId = r.DatasetId AND o.ContentHash = r.ContentHash " +
                $"AND o.Status <> {AnalysisSql.Archived} AND (o.Ordinal < r.Ordinal OR (o.Ordinal = r.Ordinal AND o.Id < r.Id)))",
            QualityRule.ConflictingAnswer => $"a.RecordId IS NOT NULL AND {AnalysisSql.IsConflicting}",
            // Keep the first record of each group of near-duplicate questions.
            QualityRule.NearDuplicate =>
                $"a.RecordId IS NOT NULL AND {AnalysisSql.IsNearDuplicate} AND EXISTS (SELECT 1 FROM RecordAnalyses a4 " +
                "JOIN DatasetRecords r4 ON r4.Id = a4.RecordId AND a4.ContentHash = r4.ContentHash " +
                $"WHERE a4.DatasetId = r.DatasetId AND a4.NearGroup = a.NearGroup AND a4.Tokenizer = a.Tokenizer AND a4.RulesVersion = {QualityRules.Version} " +
                $"AND r4.Status <> {AnalysisSql.Archived} AND (r4.Ordinal < r.Ordinal OR (r4.Ordinal = r.Ordinal AND r4.Id < r.Id)))",
            QualityRule.OverContextLimit => $"a.RecordId IS NOT NULL AND {AnalysisSql.IsOverLimit}",
            _ => $"a.RecordId IS NOT NULL AND (a.IssueFlags & {AnalysisSql.Bit(rule)}) <> 0",
        };

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT r.Id FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
            WHERE r.DatasetId = $ds AND r.Status <> {AnalysisSql.Archived} AND {condition}
            ORDER BY r.Ordinal, r.Id;
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(SqliteValues.ReadGuid(reader.GetString(0)));
        }

        return ids;
    }

    public async Task<IReadOnlyList<string>> GetCustomTermsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadCustomTermsAsync(connection, cancellationToken);
    }

    public async Task SetCustomTermsAsync(IReadOnlyList<string> terms, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var cleaned = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO WorkspaceSettings (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """;
            command.Parameters.AddWithValue("$key", CustomTermsKey);
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(cleaned));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // The personal-data rule depends on the terms, so every stored result is out of date.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM RecordAnalyses;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<int> AnalyzeDatasetAsync(Guid datasetId, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var tokenizer = await ReadTokenizerAsync(connection, datasetId, cancellationToken)
            ?? throw new NotFoundException(nameof(Dataset), datasetId);
        var analyzer = new RecordAnalyzer(tokenizers.Get(tokenizer), new PiiScanner(await ReadCustomTermsAsync(connection, cancellationToken)));

        var done = 0;
        (long Ordinal, string Id)? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await ReadPendingAsync(connection, datasetId, after, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            await WriteAsync(connection, Analyze(analyzer, batch), tokenizer, cancellationToken);
            done += batch.Count;
            progress?.Report(done);
            after = (batch[^1].Ordinal, batch[^1].Id);
        }

        await UpdateNearGroupsAsync(connection, datasetId, cancellationToken);
        return done;
    }

    private async Task AnalyzeRecordsCoreAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.Id, r.Ordinal, r.DatasetId, r.ContentHash, r.DataPayload, d.DefaultTokenizer
            FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId
            WHERE r.Id IN (SELECT value FROM json_each($ids));
            """;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(recordIds.Select(SqliteValues.Guid)));

        var rows = new List<(PendingRecord Record, string Tokenizer)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((new PendingRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), reader.GetString(5)));
            }
        }

        var scanner = new PiiScanner(await ReadCustomTermsAsync(connection, cancellationToken));
        foreach (var group in rows.GroupBy(r => r.Tokenizer, StringComparer.Ordinal))
        {
            var analyzer = new RecordAnalyzer(tokenizers.Get(group.Key), scanner);
            await WriteAsync(connection, Analyze(analyzer, group.Select(r => r.Record).ToList()), group.Key, cancellationToken);
        }
    }

    /// <summary>
    /// Groups the near-duplicate questions of a dataset again (SDD §8.2) when a question changed
    /// since the last time. Records with an up-to-date analysis take part, archived ones too, so
    /// archiving or restoring needs no new pass. Each record stores the first record of its group,
    /// or nothing when every record of its group asks exactly the same question.
    /// </summary>
    /// <returns><c>true</c> if any record's group changed.</returns>
    private static async Task<bool> UpdateNearGroupsAsync(SqliteConnection connection, Guid datasetId, CancellationToken cancellationToken)
    {
        var ds = SqliteValues.Guid(datasetId);

        // Which contents are there: one light read decides whether anything has to be done.
        var records = new List<(string Id, string ContentHash, string? QuestionKey, string? NearGroup)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT r.Id, r.ContentHash, a.QuestionKey, a.NearGroup
                FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
                WHERE r.DatasetId = $ds AND a.Signature IS NOT NULL
                ORDER BY r.Ordinal, r.Id;
                """;
            command.Parameters.AddWithValue("$ds", ds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var stamp = Stamp(records.Select(r => (r.Id, r.ContentHash)));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Stamp FROM DatasetSimilarity WHERE DatasetId = $ds;";
            command.Parameters.AddWithValue("$ds", ds);
            if (await command.ExecuteScalarAsync(cancellationToken) as string == stamp)
            {
                return false;
            }
        }

        var keys = records.Select(r => r.QuestionKey).ToList();
        var candidates = NearDuplicates.Candidates(await ReadSignaturesAsync(connection, [.. records.Select(r => r.Id)], cancellationToken), keys);

        // The exact check needs the questions of the candidates only.
        var involved = candidates.SelectMany(p => new[] { p.A, p.B }).Distinct().ToDictionary(i => records[i].Id, i => i, StringComparer.Ordinal);
        var shingles = await ReadShinglesAsync(connection, involved, cancellationToken);
        var roots = NearDuplicates.Group(records.Count, keys, candidates, (a, b) => NearDuplicates.Jaccard(shingles[a], shingles[b]) >= NearDuplicates.Threshold);

        // A group whose records all ask exactly the same question has no near-duplicate in it.
        var mixed = roots.Select((root, i) => (root, key: keys[i])).GroupBy(x => x.root)
            .Where(g => g.Select(x => x.key).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(g => g.Key)
            .ToHashSet();

        var changed = false;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // Only while the content is still the one compared; a record edited meanwhile waits for the next pass.
            command.CommandText = "UPDATE RecordAnalyses SET NearGroup = $group WHERE RecordId = $id AND ContentHash = $hash;";
            var group = command.Parameters.Add(new SqliteParameter { ParameterName = "$group" });
            var id = command.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
            var hash = command.Parameters.Add(new SqliteParameter { ParameterName = "$hash" });
            command.Prepare();
            for (var i = 0; i < records.Count; i++)
            {
                var near = mixed.Contains(roots[i]) ? records[roots[i]].Id : null;
                if (near != records[i].NearGroup)
                {
                    group.Value = SqliteValues.NullableText(near);
                    id.Value = records[i].Id;
                    hash.Value = records[i].ContentHash;
                    changed |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO DatasetSimilarity (DatasetId, Stamp) VALUES ($ds, $stamp) ON CONFLICT(DatasetId) DO UPDATE SET Stamp = excluded.Stamp;";
            command.Parameters.AddWithValue("$ds", ds);
            command.Parameters.AddWithValue("$stamp", stamp);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    // What the groups were made from. The rules version is part of it: new rules may compare differently.
    private static string Stamp(IEnumerable<(string Id, string ContentHash)> records)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"rules:{QualityRules.Version}\n"));
        foreach (var (id, contentHash) in records)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{id}:{contentHash}\n"));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<List<byte[]?>> ReadSignaturesAsync(SqliteConnection connection, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, byte[]>(ids.Count, StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(BatchSize * 10))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT RecordId, Signature FROM RecordAnalyses WHERE RecordId IN (SELECT value FROM json_each($ids));";
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(chunk));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(1))
                {
                    byId[reader.GetString(0)] = (byte[])reader.GetValue(1);
                }
            }
        }

        // A signature of another length (a damaged row) takes no part.
        return [.. ids.Select(id => byId.TryGetValue(id, out var signature) && signature.Length == NearDuplicates.SignatureBytes ? signature : null)];
    }

    // The 5-grams of the candidates' questions, by record index.
    private static async Task<Dictionary<int, HashSet<ulong>>> ReadShinglesAsync(
        SqliteConnection connection, IReadOnlyDictionary<string, int> indexes, CancellationToken cancellationToken)
    {
        var shingles = new Dictionary<int, HashSet<ulong>>(indexes.Count);
        foreach (var chunk in indexes.Keys.Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, DataPayload FROM DatasetRecords WHERE Id IN (SELECT value FROM json_each($ids));";
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(chunk));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var question = RecordAnalyzer.QuestionText(PayloadJson.Deserialize(reader.GetString(1))) ?? string.Empty;
                shingles[indexes[reader.GetString(0)]] = NearDuplicates.Shingles(RecordAnalyzer.Normalize(question));
            }
        }

        // A record deleted meanwhile compares as empty.
        foreach (var index in indexes.Values)
        {
            shingles.TryAdd(index, []);
        }

        return shingles;
    }

    // Batches are analysed in parallel; tokenizers and the scanner are thread-safe.
    private static List<(PendingRecord Record, RecordAnalysisResult Result)> Analyze(RecordAnalyzer analyzer, IReadOnlyList<PendingRecord> batch) =>
        batch.AsParallel().AsOrdered()
            .Select(record => (record, analyzer.Analyze(PayloadJson.Deserialize(record.Payload))))
            .ToList();

    private static async Task<List<PendingRecord>> ReadPendingAsync(
        SqliteConnection connection,
        Guid datasetId,
        (long Ordinal, string Id)? after,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var keyset = after is null ? string.Empty : "AND (r.Ordinal > $o OR (r.Ordinal = $o AND r.Id > $id))";
        command.CommandText = $"""
            SELECT r.Id, r.Ordinal, r.DatasetId, r.ContentHash, r.DataPayload
            FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
            WHERE r.DatasetId = $ds AND a.RecordId IS NULL {keyset}
            ORDER BY r.Ordinal, r.Id LIMIT {BatchSize};
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        if (after is { } cursor)
        {
            command.Parameters.AddWithValue("$o", cursor.Ordinal);
            command.Parameters.AddWithValue("$id", cursor.Id);
        }

        var batch = new List<PendingRecord>(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            batch.Add(new PendingRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return batch;
    }

    private async Task WriteAsync(
        SqliteConnection connection,
        IReadOnlyList<(PendingRecord Record, RecordAnalysisResult Result)> results,
        string tokenizer,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Records deleted since they were read are skipped instead of failing the batch.
        command.CommandText = """
            INSERT INTO RecordAnalyses (RecordId, DatasetId, ContentHash, Tokenizer, RulesVersion, InputTokens, OutputTokens,
                                        TotalTokens, QuestionKey, Signature, Score, IssueFlags, Issues, AnalyzedAt)
            SELECT $id, $ds, $hash, $tokenizer, $rules, $input, $output, $total, $question, $signature, $score, $flags, $issues, $at
            WHERE EXISTS (SELECT 1 FROM DatasetRecords WHERE Id = $id)
            ON CONFLICT(RecordId) DO UPDATE SET
                DatasetId = excluded.DatasetId, ContentHash = excluded.ContentHash, Tokenizer = excluded.Tokenizer,
                RulesVersion = excluded.RulesVersion, InputTokens = excluded.InputTokens, OutputTokens = excluded.OutputTokens,
                TotalTokens = excluded.TotalTokens, QuestionKey = excluded.QuestionKey, Signature = excluded.Signature,
                NearGroup = CASE WHEN RecordAnalyses.ContentHash = excluded.ContentHash THEN RecordAnalyses.NearGroup END,
                Score = excluded.Score, IssueFlags = excluded.IssueFlags, Issues = excluded.Issues, AnalyzedAt = excluded.AnalyzedAt;
            """;
        // The near-duplicate group stays while the content does; otherwise the next grouping pass decides.
        foreach (var name in new[] { "$id", "$ds", "$hash", "$tokenizer", "$rules", "$input", "$output", "$total", "$question", "$signature", "$score", "$flags", "$issues", "$at" })
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        command.Prepare();
        var now = SqliteValues.Timestamp(timeProvider.GetUtcNow());
        var p = command.Parameters;
        foreach (var (record, result) in results)
        {
            p["$id"].Value = record.Id;
            p["$ds"].Value = record.DatasetId;
            p["$hash"].Value = record.ContentHash;
            p["$tokenizer"].Value = tokenizer;
            p["$rules"].Value = QualityRules.Version;
            p["$input"].Value = result.Tokens.Input;
            p["$output"].Value = result.Tokens.Output;
            p["$total"].Value = result.Tokens.Total;
            p["$question"].Value = SqliteValues.NullableText(result.QuestionKey);
            p["$signature"].Value = result.Signature is { } signature ? signature : DBNull.Value;
            p["$score"].Value = result.Score;
            p["$flags"].Value = result.Issues.Aggregate(0, (flags, issue) => flags | AnalysisSql.Bit(issue.Rule));
            p["$issues"].Value = JsonSerializer.Serialize(result.Issues, IssueJson);
            p["$at"].Value = now;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string?> ReadTokenizerAsync(SqliteConnection connection, Guid datasetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DefaultTokenizer FROM Datasets WHERE Id = $ds;";
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<IReadOnlyList<string>> ReadCustomTermsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM WorkspaceSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", CustomTermsKey);
        return await command.ExecuteScalarAsync(cancellationToken) is string json
            ? JsonSerializer.Deserialize<List<string>>(json) ?? []
            : [];
    }

    private static List<QualityIssue> ReadIssues(string json) => JsonSerializer.Deserialize<List<QualityIssue>>(json, IssueJson) ?? [];

    private static QualityIssue Issue(QualityRule rule, string? detail = null) => new(rule, QualityRules.SeverityOf(rule), detail);

    /// <summary>
    /// A record to analyse, with ids as stored (upper-case text).
    /// </summary>
    private sealed record PendingRecord(string Id, long Ordinal, string DatasetId, string ContentHash, string Payload);
}
