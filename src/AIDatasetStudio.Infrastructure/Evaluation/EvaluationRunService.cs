using System.Text.Json;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Infrastructure.Persistence;

namespace AIDatasetStudio.Infrastructure.Evaluation;

/// <summary>
/// Keeps the summaries of a dataset's model evaluations (SDD §17.10), deleted with the dataset.
/// </summary>
internal sealed class EvaluationRunService(IWorkspaceDatabase database, TimeProvider timeProvider) : IEvaluationRunService
{
    public async Task<EvaluationRun> AddAsync(
        Guid datasetId, string model, string judge, EvaluationSource source, int seed, EvaluationSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(judge);
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Counts.Count != Judging.MaxScore)
        {
            throw new ArgumentException("A summary counts each score from 1 to 5.", nameof(summary));
        }

        // Stored to the millisecond; the run comes back as it will be read.
        var at = SqliteValues.ReadTimestamp(SqliteValues.Timestamp(timeProvider.GetUtcNow()));
        var run = new EvaluationRun(Guid.CreateVersion7(), model, judge, source, seed, summary, at);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO EvaluationRuns (Id, DatasetId, Model, Judge, Source, Seed, Answered, Scored, AverageScore, AverageF1, Counts, RanAt)
            SELECT $id, d.Id, $model, $judge, $source, $seed, $answered, $scored, $score, $f1, $counts, $at FROM Datasets d WHERE d.Id = $ds;
            """;
        command.Parameters.AddWithValue("$id", SqliteValues.Guid(run.Id));
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$judge", judge);
        command.Parameters.AddWithValue("$source", (int)source);
        command.Parameters.AddWithValue("$seed", seed);
        command.Parameters.AddWithValue("$answered", summary.Answered);
        command.Parameters.AddWithValue("$scored", summary.Scored);
        command.Parameters.AddWithValue("$score", summary.AverageScore is { } score ? score : DBNull.Value);
        command.Parameters.AddWithValue("$f1", summary.AverageF1 is { } f1 ? f1 : DBNull.Value);
        command.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(summary.Counts));
        command.Parameters.AddWithValue("$at", SqliteValues.Timestamp(at));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return run;
    }

    public async Task<IReadOnlyList<EvaluationRun>> GetRunsAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Model, Judge, Source, Seed, Answered, Scored, AverageScore, AverageF1, Counts, RanAt
            FROM EvaluationRuns WHERE DatasetId = $ds ORDER BY RanAt DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        var runs = new List<EvaluationRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var summary = new EvaluationSummary(
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                JsonSerializer.Deserialize<int[]>(reader.GetString(9)) ?? new int[Judging.MaxScore]);
            runs.Add(new EvaluationRun(
                SqliteValues.ReadGuid(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                (EvaluationSource)reader.GetInt32(3),
                reader.GetInt32(4),
                summary,
                SqliteValues.ReadTimestamp(reader.GetInt64(10))));
        }

        return runs;
    }

    public async Task DeleteAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM EvaluationRuns WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", SqliteValues.Guid(runId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
