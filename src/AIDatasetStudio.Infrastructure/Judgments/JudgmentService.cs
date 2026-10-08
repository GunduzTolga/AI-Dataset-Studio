using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Infrastructure.Analysis;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Configurations;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Judgments;

/// <summary>
/// Stores judgments and contradictions per record with the content they were made of (SDD §17.7).
/// </summary>
internal sealed class JudgmentService(IWorkspaceDatabase database) : IJudgmentService
{
    public async Task SaveAsync(RecordJudgment judgment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(judgment);
        ArgumentOutOfRangeException.ThrowIfLessThan(judgment.Score, Judging.MinScore);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(judgment.Score, Judging.MaxScore);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // A record deleted while the model judged it gets nothing.
        command.CommandText = """
            INSERT INTO RecordJudgments (RecordId, DatasetId, ContentHash, Score, Reason, Flags, Model, JudgedAt)
            SELECT r.Id, r.DatasetId, $hash, $score, $reason, $flags, $model, $at FROM DatasetRecords r WHERE r.Id = $id
            ON CONFLICT(RecordId) DO UPDATE SET ContentHash = excluded.ContentHash, Score = excluded.Score, Reason = excluded.Reason,
                Flags = excluded.Flags, Model = excluded.Model, JudgedAt = excluded.JudgedAt;
            """;
        command.Parameters.AddWithValue("$id", SqliteValues.Guid(judgment.RecordId));
        command.Parameters.AddWithValue("$hash", judgment.ContentHash);
        command.Parameters.AddWithValue("$score", judgment.Score);
        command.Parameters.AddWithValue("$reason", Cut(judgment.Reason));
        command.Parameters.AddWithValue("$flags", (int)judgment.Flags);
        command.Parameters.AddWithValue("$model", judgment.Model);
        command.Parameters.AddWithValue("$at", SqliteValues.Timestamp(judgment.JudgedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveConflictAsync(RecordConflict conflict, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (conflict.RecordId == conflict.OtherId)
        {
            throw new ArgumentException("A record cannot be compared with itself.", nameof(conflict));
        }

        // A pair is kept once, with the smaller id first.
        var (first, second) = (SqliteValues.Guid(conflict.RecordId), SqliteValues.Guid(conflict.OtherId));
        var (firstHash, secondHash) = (conflict.RecordHash, conflict.OtherHash);
        if (string.CompareOrdinal(first, second) > 0)
        {
            (first, second, firstHash, secondHash) = (second, first, secondHash, firstHash);
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO RecordConflicts (RecordId, OtherId, DatasetId, RecordHash, OtherHash, Contradicts, Reason, Model, CheckedAt)
            SELECT r.Id, o.Id, r.DatasetId, $firstHash, $secondHash, $contradicts, $reason, $model, $at
            FROM DatasetRecords r JOIN DatasetRecords o ON o.Id = $second WHERE r.Id = $first
            ON CONFLICT(RecordId, OtherId) DO UPDATE SET RecordHash = excluded.RecordHash, OtherHash = excluded.OtherHash,
                Contradicts = excluded.Contradicts, Reason = excluded.Reason, Model = excluded.Model, CheckedAt = excluded.CheckedAt;
            """;
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$second", second);
        command.Parameters.AddWithValue("$firstHash", firstHash);
        command.Parameters.AddWithValue("$secondHash", secondHash);
        command.Parameters.AddWithValue("$contradicts", conflict.Contradicts);
        command.Parameters.AddWithValue("$reason", Cut(conflict.Reason));
        command.Parameters.AddWithValue("$model", conflict.Model);
        command.Parameters.AddWithValue("$at", SqliteValues.Timestamp(conflict.CheckedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JudgmentDetails> GetAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        var id = SqliteValues.Guid(recordId);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        RecordJudgment? judgment = null;
        var current = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT j.ContentHash, j.Score, j.Reason, j.Flags, j.Model, j.JudgedAt, r.ContentHash
                FROM RecordJudgments j JOIN DatasetRecords r ON r.Id = j.RecordId WHERE j.RecordId = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                judgment = new RecordJudgment(
                    recordId,
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    (JudgeFlags)reader.GetInt32(3),
                    reader.GetString(4),
                    SqliteValues.ReadTimestamp(reader.GetInt64(5)));
                current = judgment.ContentHash == reader.GetString(6);
            }
        }

        var contradictions = new List<ContradictionDetails>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
                SELECT o.Id, o.Ordinal, c.Reason, c.Model, c.CheckedAt
                FROM RecordConflicts c JOIN DatasetRecords r ON r.Id = c.RecordId JOIN DatasetRecords o ON o.Id = c.OtherId
                WHERE c.RecordId = $id AND c.Contradicts = 1 AND c.RecordHash = r.ContentHash AND c.OtherHash = o.ContentHash AND o.Status <> {AnalysisSql.Archived}
                UNION ALL
                SELECT o.Id, o.Ordinal, c.Reason, c.Model, c.CheckedAt
                FROM RecordConflicts c JOIN DatasetRecords r ON r.Id = c.OtherId JOIN DatasetRecords o ON o.Id = c.RecordId
                WHERE c.OtherId = $id AND c.Contradicts = 1 AND c.OtherHash = r.ContentHash AND c.RecordHash = o.ContentHash AND o.Status <> {AnalysisSql.Archived}
                ORDER BY 2;
                """);
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                contradictions.Add(new ContradictionDetails(
                    SqliteValues.ReadGuid(reader.GetString(0)),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    SqliteValues.ReadTimestamp(reader.GetInt64(4))));
            }
        }

        return judgment is null && contradictions.Count == 0 ? JudgmentDetails.None : new JudgmentDetails(judgment, current, contradictions);
    }

    public async Task<IReadOnlyList<SiblingRecord>> FindSiblingsAsync(Guid recordId, int max, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Records that ask the same question or one of its near duplicates, as analysed now, and
        // the record's family; not those already compared with it as both are now.
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
            SELECT r.DataPayload, o.Id, o.Ordinal, o.ContentHash, o.DataPayload
            FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
            JOIN DatasetRecords o ON o.DatasetId = r.DatasetId AND o.Id <> r.Id AND o.ContentHash <> r.ContentHash AND o.Status <> {AnalysisSql.Archived}
            LEFT JOIN RecordAnalyses oa ON oa.RecordId = o.Id AND oa.ContentHash = o.ContentHash AND oa.Tokenizer = d.DefaultTokenizer
                AND oa.RulesVersion = {QualityRules.Version}
            WHERE r.Id = $id
              AND ((a.QuestionKey IS NOT NULL AND oa.QuestionKey = a.QuestionKey)
                OR (a.NearGroup IS NOT NULL AND oa.NearGroup = a.NearGroup)
                OR {AnalysisSql.Family("o")} = {AnalysisSql.Family("r")})
              AND NOT EXISTS (SELECT 1 FROM RecordConflicts c
                WHERE (c.RecordId = r.Id AND c.OtherId = o.Id AND c.RecordHash = r.ContentHash AND c.OtherHash = o.ContentHash)
                   OR (c.RecordId = o.Id AND c.OtherId = r.Id AND c.RecordHash = o.ContentHash AND c.OtherHash = r.ContentHash))
            ORDER BY o.Ordinal, o.Id;
            """);
        command.Parameters.AddWithValue("$id", SqliteValues.Guid(recordId));

        var siblings = new List<SiblingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (siblings.Count < max && await reader.ReadAsync(cancellationToken))
        {
            // Two records that give the same answer cannot contradict each other (variations often do).
            var answer = Variations.SourceOf(PayloadJson.Deserialize(reader.GetString(0)))?.Answer;
            var payload = PayloadJson.Deserialize(reader.GetString(4));
            if (answer is not { Length: > 0 } || Variations.SourceOf(payload) is not { Answer.Length: > 0 } other || Variations.AreSame(answer, other.Answer))
            {
                continue;
            }

            siblings.Add(new SiblingRecord(SqliteValues.ReadGuid(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3), payload));
        }

        return siblings;
    }

    public async Task<JudgmentSummary> GetSummaryAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        const string Current = "(j.RecordId IS NOT NULL AND j.ContentHash = r.ContentHash)";
        var scores = string.Concat(Enumerable.Range(Judging.MinScore, Judging.MaxScore).Select(score =>
            string.Create(CultureInfo.InvariantCulture, $", COALESCE(SUM({Current} AND j.Score = {score}), 0)")));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
            SELECT COUNT(*),
                   COALESCE(SUM({Current}), 0),
                   COALESCE(SUM(j.RecordId IS NOT NULL AND j.ContentHash <> r.ContentHash), 0),
                   AVG(CASE WHEN {Current} THEN j.Score END),
                   COALESCE(SUM({Current} AND (j.Flags & {(int)JudgeFlags.Ungrounded}) <> 0), 0),
                   COALESCE(SUM({Current} AND (j.Flags & {(int)(JudgeFlags.RejectedBetter | JudgeFlags.NoPreference)}) <> 0), 0),
                   COALESCE(SUM({JudgmentSql.HasContradiction}), 0){scores}
            FROM DatasetRecords r LEFT JOIN RecordJudgments j ON j.RecordId = r.Id
            WHERE r.DatasetId = $ds AND r.Status <> {AnalysisSql.Archived};
            """);
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new JudgmentSummary(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            [.. Enumerable.Range(7, Judging.MaxScore).Select(reader.GetInt64)],
            reader.IsDBNull(3) ? null : reader.GetDouble(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    public async Task<IReadOnlyList<Guid>> FindNotJudgedAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
            SELECT r.Id FROM DatasetRecords r
            WHERE r.DatasetId = $ds AND r.Status <> {AnalysisSql.Archived} AND {JudgmentSql.Condition(QualityFilter.NotJudged)}
            ORDER BY r.Ordinal, r.Id;
            """);
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(SqliteValues.ReadGuid(reader.GetString(0)));
        }

        return ids;
    }

    private static string Cut(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= RecordJudgmentConfiguration.MaxReasonLength ? trimmed : trimmed[..(RecordJudgmentConfiguration.MaxReasonLength - 1)] + "…";
    }
}
