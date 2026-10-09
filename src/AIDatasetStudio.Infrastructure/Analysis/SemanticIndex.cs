using System.Runtime.InteropServices;
using System.Text.Json;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Analysis;

/// <summary>
/// Keeps the vectors of a dataset's questions in QuestionEmbeddings and the dataset's choice in
/// DatasetSimilarity (SDD §8.2, M16). The questions are those the near-duplicate pass groups:
/// records with an up-to-date analysis and a question, archived ones too.
/// </summary>
internal sealed class SemanticIndex(IWorkspaceDatabase database) : ISemanticIndex
{
    // The analysed questions of the dataset, one row per record.
    private static readonly string Questions = $"""
        FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
        WHERE r.DatasetId = $ds AND a.Signature IS NOT NULL
        """;

    public async Task<SemanticState> GetStateAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.SemanticModel, s.SemanticThreshold,
                   (SELECT COUNT(DISTINCT a.QuestionKey) {Questions}),
                   (SELECT COUNT(DISTINCT a.QuestionKey) {Questions}
                    AND EXISTS (SELECT 1 FROM QuestionEmbeddings e
                                WHERE e.DatasetId = $ds AND e.QuestionKey = a.QuestionKey AND e.Model = s.SemanticModel))
            FROM (SELECT 1) LEFT JOIN DatasetSimilarity s ON s.DatasetId = $ds;
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new SemanticState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? SemanticQuestions.DefaultThreshold : reader.GetDouble(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    public async Task SetAsync(Guid datasetId, string? model, double threshold, CancellationToken cancellationToken = default)
    {
        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        threshold = double.IsFinite(threshold)
            ? Math.Clamp(threshold, SemanticQuestions.MinThreshold, SemanticQuestions.MaxThreshold)
            : SemanticQuestions.DefaultThreshold;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // The stamp stays: it includes the choice, so the next pass sees whether anything changed.
            command.CommandText = """
                INSERT INTO DatasetSimilarity (DatasetId, Stamp, SemanticModel, SemanticThreshold)
                SELECT Id, '', $model, $threshold FROM Datasets WHERE Id = $ds
                ON CONFLICT(DatasetId) DO UPDATE SET SemanticModel = excluded.SemanticModel, SemanticThreshold = excluded.SemanticThreshold;
                """;
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
            command.Parameters.AddWithValue("$model", SqliteValues.NullableText(model));
            command.Parameters.AddWithValue("$threshold", threshold);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new NotFoundException(nameof(Dataset), datasetId);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // Turned off, the vectors stay for turning on again; another model makes them useless.
            command.CommandText = """
                DELETE FROM QuestionEmbeddings
                WHERE DatasetId = $ds AND (($model IS NOT NULL AND Model <> $model)
                    OR NOT EXISTS (SELECT 1 FROM RecordAnalyses a WHERE a.DatasetId = $ds AND a.QuestionKey = QuestionEmbeddings.QuestionKey));
                """;
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
            command.Parameters.AddWithValue("$model", SqliteValues.NullableText(model));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<IReadOnlyList<PendingQuestion>> FindPendingAsync(Guid datasetId, int max, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        // SQLite's async calls complete synchronously, and this reads the whole dataset: keep it
        // off the caller's (UI) thread.
        return Task.Run(() => FindPendingCoreAsync(datasetId, max, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<PendingQuestion>> FindPendingCoreAsync(Guid datasetId, int max, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // SQLite takes the bare columns from the row of MIN(): each question's first record.
        command.CommandText = $"""
            SELECT a.QuestionKey, r.DataPayload, MIN(r.Ordinal) AS First
            {Questions}
            AND EXISTS (SELECT 1 FROM DatasetSimilarity s WHERE s.DatasetId = $ds AND s.SemanticModel IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM QuestionEmbeddings e
                                WHERE e.DatasetId = $ds AND e.QuestionKey = a.QuestionKey AND e.Model = s.SemanticModel))
            GROUP BY a.QuestionKey
            ORDER BY First
            LIMIT $max;
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        command.Parameters.AddWithValue("$max", max);
        var pending = new List<PendingQuestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (SemanticQuestions.Text(PayloadJson.Deserialize(reader.GetString(1))) is { } text)
            {
                pending.Add(new PendingQuestion(reader.GetString(0), text));
            }
        }

        return pending;
    }

    public async Task<int> SaveAsync(Guid datasetId, string model, IReadOnlyList<QuestionEmbedding> embeddings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(embeddings);
        model = model.Trim();

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT SemanticModel FROM DatasetSimilarity WHERE DatasetId = $ds;";
            check.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
            if (await check.ExecuteScalarAsync(cancellationToken) as string != model)
            {
                return 0;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO QuestionEmbeddings (DatasetId, QuestionKey, Model, Vector, Scale, Signature)
            VALUES ($ds, $key, $model, $vector, $scale, $signature)
            ON CONFLICT(DatasetId, QuestionKey) DO UPDATE SET Model = excluded.Model, Vector = excluded.Vector,
                Scale = excluded.Scale, Signature = excluded.Signature;
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        command.Parameters.AddWithValue("$model", model);
        var key = command.Parameters.Add(new SqliteParameter { ParameterName = "$key" });
        var vector = command.Parameters.Add(new SqliteParameter { ParameterName = "$vector" });
        var scale = command.Parameters.Add(new SqliteParameter { ParameterName = "$scale" });
        var signature = command.Parameters.Add(new SqliteParameter { ParameterName = "$signature" });
        command.Prepare();
        var kept = 0;
        foreach (var embedding in embeddings)
        {
            QuestionVector encoded;
            try
            {
                encoded = SemanticQuestions.Encode(embedding.Embedding);
            }
            catch (ArgumentException)
            {
                // A vector of zeros (or worse) compares with nothing; the question stays without one.
                continue;
            }

            key.Value = embedding.QuestionKey;
            vector.Value = MemoryMarshal.AsBytes(encoded.Values.AsSpan()).ToArray();
            scale.Value = (double)encoded.Scale;
            signature.Value = encoded.Signature;
            kept += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return kept;
    }

    /// <summary>
    /// The signatures of the dataset's questions with a vector from the model, by question key.
    /// </summary>
    internal static async Task<Dictionary<string, byte[]>> ReadSignaturesAsync(
        SqliteConnection connection, string datasetId, string model, CancellationToken cancellationToken)
    {
        var signatures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT QuestionKey, Signature FROM QuestionEmbeddings WHERE DatasetId = $ds AND Model = $model;";
        command.Parameters.AddWithValue("$ds", datasetId);
        command.Parameters.AddWithValue("$model", model);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            signatures[reader.GetString(0)] = (byte[])reader.GetValue(1);
        }

        return signatures;
    }

    /// <summary>
    /// The vectors of the given questions from the model, by question key.
    /// </summary>
    internal static async Task<Dictionary<string, QuestionVector>> ReadVectorsAsync(
        SqliteConnection connection, string datasetId, string model, IEnumerable<string> questionKeys, CancellationToken cancellationToken)
    {
        var vectors = new Dictionary<string, QuestionVector>(StringComparer.Ordinal);
        foreach (var chunk in questionKeys.Chunk(5_000))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT QuestionKey, Vector, Scale, Signature FROM QuestionEmbeddings
                WHERE DatasetId = $ds AND Model = $model AND QuestionKey IN (SELECT value FROM json_each($keys));
                """;
            command.Parameters.AddWithValue("$ds", datasetId);
            command.Parameters.AddWithValue("$model", model);
            command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(chunk));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = MemoryMarshal.Cast<byte, sbyte>((byte[])reader.GetValue(1)).ToArray();
                vectors[reader.GetString(0)] = new QuestionVector(values, (float)reader.GetDouble(2), (byte[])reader.GetValue(3));
            }
        }

        return vectors;
    }
}
