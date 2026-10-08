using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Infrastructure.Analysis;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using AIDatasetStudio.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Export;

/// <summary>
/// Streams a dataset into JSON Lines files (SDD §10, §13). Records are read in their list order
/// from one consistent snapshot, filtered, optionally masked, converted and assigned to a split
/// part; files are written next to their destination and moved into place only when complete.
/// </summary>
internal sealed class ExportService(IWorkspaceDatabase database, IRecordAnalysisService analysis, TimeProvider timeProvider) : IExportService
{
    private const int BatchSize = 500;
    private const string PartialSuffix = ".partial";

    // Analysed and longer than the dataset's context length (never NULL).
    private const string OverLimitExpression = "(a.RecordId IS NOT NULL AND a.TotalTokens > d.ContextLength)";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ExportPreview> PreviewAsync(Guid datasetId, ExportOptions options, int sampleLines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleLines);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var dataset = await ReadDatasetAsync(connection, datasetId, cancellationToken);
        Validate(options, dataset.Type);
        if (options.Statuses.Count == 0)
        {
            return new ExportPreview(0, 0, 0, 0, 0, []);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*),
                   COALESCE(SUM({UnsupportedSql(options, dataset.Type)}), 0),
                   COALESCE(SUM(NOT {UnsupportedSql(options, dataset.Type)} AND {OverLimitSql(options)}), 0),
                   COALESCE(SUM(NOT {UnsupportedSql(options, dataset.Type)} AND NOT {OverLimitSql(options)} AND {DuplicateSql(options)}), 0),
                   COALESCE(SUM({(options.SkipOverContextLimit ? "a.RecordId IS NULL" : "0")}), 0)
            FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
            WHERE r.DatasetId = $ds AND r.Status IN ({StatusList(options)});
            """;
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        long selected, unsupported, overLimit, duplicates, notAnalyzed;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            (selected, unsupported, overLimit, duplicates, notAnalyzed) =
                (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }

        var samples = new List<string>(sampleLines);
        if (sampleLines > 0)
        {
            var scanner = options.MaskPersonalData ? new PiiScanner(await analysis.GetCustomTermsAsync(cancellationToken)) : null;
            var pipeline = new Pipeline(options, scanner);
            await foreach (var record in ReadAsync(connection, null, datasetId, options, cancellationToken))
            {
                if (pipeline.Convert(record) is { } line)
                {
                    samples.Add(line.Text);
                    if (samples.Count == sampleLines)
                    {
                        break;
                    }
                }
            }
        }

        return new ExportPreview(selected, unsupported, overLimit, duplicates, notAnalyzed, samples);
    }

    public Task<ExportResult> ExportAsync(
        Guid datasetId,
        ExportOptions options,
        string destination,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        // Converting and masking is CPU work; keep it off the caller's (usually the UI) thread.
        return Task.Run(() => ExportCoreAsync(datasetId, options, Path.GetFullPath(destination), progress, cancellationToken), cancellationToken);
    }

    public async Task<ExportOptions?> GetLastOptionsAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var row = await context.ExportProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.DatasetId == datasetId, cancellationToken);
        return row is null ? null : ReadOptions(row.Settings);
    }

    public async Task<ExportHistoryEntry?> GetLastExportAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var row = await context.ExportHistories.AsNoTracking()
            .Where(h => h.DatasetId == datasetId)
            .OrderByDescending(h => h.ExportedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var files = JsonSerializer.Deserialize<List<ExportedFile>>(row.Files, SettingsJson) ?? [];
        return new ExportHistoryEntry(row.TargetFormat, row.RecordCount, [.. files.Select(f => f.Path)], row.ExportedAt);
    }

    private async Task<ExportResult> ExportCoreAsync(
        Guid datasetId, ExportOptions options, string destination, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        DatasetInfo dataset;
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        {
            dataset = await ReadDatasetAsync(connection, datasetId, cancellationToken);
        }

        Validate(options, dataset.Type);

        // The length check needs every record's token count with the dataset's tokenizer.
        if (options.SkipOverContextLimit && await analysis.CountPendingAsync(datasetId, cancellationToken) > 0)
        {
            await analysis.AnalyzeAsync(datasetId, cancellationToken: cancellationToken);
        }

        var scanner = options.MaskPersonalData ? new PiiScanner(await analysis.GetCustomTermsAsync(cancellationToken)) : null;
        var targets = ExportFormats.Files(options, destination);
        if (options.Split is not null)
        {
            Directory.CreateDirectory(destination);
        }

        var files = targets.Select(t => new PartFile(t.Part, t.Path)).ToList();
        try
        {
            foreach (var file in files)
            {
                file.Open();
            }

            var pipeline = new Pipeline(options, scanner);
            await using (var connection = await database.OpenConnectionAsync(cancellationToken))
            {
                // One deferred read transaction: a consistent snapshot that does not block edits (WAL).
                await using var snapshot = connection.BeginTransaction(deferred: true);
                var total = await CountSelectedAsync(connection, snapshot, datasetId, options, cancellationToken);
                long processed = 0;
                progress?.Report(new ExportProgress(0, total));
                await foreach (var record in ReadAsync(connection, snapshot, datasetId, options, cancellationToken))
                {
                    if (pipeline.Convert(record) is { } line)
                    {
                        await files.First(f => f.Part == line.Part).WriteAsync(line.Text, cancellationToken);
                    }

                    if (++processed % BatchSize == 0)
                    {
                        progress?.Report(new ExportProgress(processed, total));
                    }
                }

                progress?.Report(new ExportProgress(processed, total));
            }

            // Complete: close every file, then move them all into place.
            foreach (var file in files)
            {
                await file.CloseAsync();
            }

            foreach (var file in files)
            {
                File.Move(file.PartialPath, file.Path, overwrite: true);
            }

            var result = new ExportResult(
                [.. files.Select(f => new ExportedFile(f.Part, f.Path, f.Count))],
                pipeline.Exported,
                pipeline.Unsupported,
                pipeline.OverLimit,
                pipeline.Duplicates,
                pipeline.Masked);
            // The files are in place: record them even if cancellation comes now.
            await RememberAsync(datasetId, options, result, CancellationToken.None);
            return result;
        }
        finally
        {
            // Cancelled or failed: nothing half-written stays behind, earlier files are untouched.
            foreach (var file in files)
            {
                await file.CloseAsync();
                if (File.Exists(file.PartialPath))
                {
                    File.Delete(file.PartialPath);
                }
            }
        }
    }

    private async Task RememberAsync(Guid datasetId, ExportOptions options, ExportResult result, CancellationToken cancellationToken)
    {
        var settings = JsonSerializer.Serialize(options, SettingsJson);
        var now = timeProvider.GetUtcNow();
        await using var context = database.CreateDbContext();
        if (await context.ExportProfiles.FindAsync([datasetId], cancellationToken) is { } profile)
        {
            profile.TargetFormat = options.Format;
            profile.Settings = settings;
            profile.UpdatedAt = now;
        }
        else
        {
            context.ExportProfiles.Add(new ExportProfileRow { DatasetId = datasetId, TargetFormat = options.Format, Settings = settings, UpdatedAt = now });
        }

        context.ExportHistories.Add(new ExportHistoryRow
        {
            Id = Guid.CreateVersion7(now),
            DatasetId = datasetId,
            TargetFormat = options.Format,
            Settings = settings,
            Files = JsonSerializer.Serialize(result.Files, SettingsJson),
            RecordCount = result.Exported,
            ExportedAt = now,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static ExportOptions? ReadOptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExportOptions>(json, SettingsJson);
        }
        catch (JsonException)
        {
            return null; // written by another version; start from the defaults
        }
    }

    private static void Validate(ExportOptions options, DatasetType type)
    {
        if (!ExportFormats.For(type).Contains(options.Format))
        {
            throw new ArgumentException($"{type} datasets cannot be exported as {options.Format}.", nameof(options));
        }

        if (options.Split is { IsValid: false })
        {
            throw new ArgumentException("Split shares must add up to 100 with some training data.", nameof(options));
        }

        if (options.Statuses.Contains(RecordStatus.Archived))
        {
            throw new ArgumentException("Archived records are never exported.", nameof(options));
        }
    }

    private static async Task<DatasetInfo> ReadDatasetAsync(SqliteConnection connection, Guid datasetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Type FROM Datasets WHERE Id = $ds;";
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        return await command.ExecuteScalarAsync(cancellationToken) is long type
            ? new DatasetInfo((DatasetType)type)
            : throw new NotFoundException(nameof(Dataset), datasetId);
    }

    private static async Task<long> CountSelectedAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid datasetId, ExportOptions options, CancellationToken cancellationToken)
    {
        if (options.Statuses.Count == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM DatasetRecords WHERE DatasetId = $ds AND Status IN ({StatusList(options)});";
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // Records with a chosen status in list order, in batches, with whether they are over the context length.
    private static async IAsyncEnumerable<ExportRecord> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid datasetId,
        ExportOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (options.Statuses.Count == 0)
        {
            yield break;
        }

        var after = long.MinValue;
        while (true)
        {
            var batch = new List<ExportRecord>(BatchSize);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT r.Id, r.Ordinal, r.ContentHash, r.DataPayload, {OverLimitExpression}
                    FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
                    WHERE r.DatasetId = $ds AND r.Status IN ({StatusList(options)}) AND r.Ordinal > $after
                    ORDER BY r.Ordinal
                    LIMIT {BatchSize};
                    """;
                command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
                command.Parameters.AddWithValue("$after", after);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    batch.Add(new ExportRecord(
                        SqliteValues.ReadGuid(reader.GetString(0)),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetBoolean(4)));
                    after = reader.GetInt64(1);
                }
            }

            foreach (var record in batch)
            {
                yield return record;
            }

            if (batch.Count < BatchSize)
            {
                yield break;
            }
        }
    }

    private static string StatusList(ExportOptions options) =>
        string.Join(", ", options.Statuses.Distinct().Select(s => ((int)s).ToString(CultureInfo.InvariantCulture)));

    // The SQL twins of the pipeline's checks, for the preview counts; tests keep them in step.
    private static string UnsupportedSql(ExportOptions options, DatasetType type) =>
        ExportFormats.IsLossy(options.Format, type)
            ? "(NOT (json_array_length(r.DataPayload, '$.messages') = 2 AND json_extract(r.DataPayload, '$.messages[0].role') = 'user' " +
              "AND json_extract(r.DataPayload, '$.messages[1].role') = 'assistant'))"
            : "0";

    private static string OverLimitSql(ExportOptions options) => options.SkipOverContextLimit ? OverLimitExpression : "0";

    private static string DuplicateSql(ExportOptions options) =>
        options.SkipDuplicates
            ? "EXISTS (SELECT 1 FROM DatasetRecords o WHERE o.DatasetId = r.DatasetId AND o.ContentHash = r.ContentHash " +
              $"AND o.Status IN ({StatusList(options)}) AND o.Ordinal < r.Ordinal)"
            : "0";

    private sealed record DatasetInfo(DatasetType Type);

    private sealed record ExportRecord(Guid Id, string ContentHash, string Json, bool IsOverLimit);

    private readonly record struct ExportLine(SplitPart? Part, string Text);

    /// <summary>
    /// One output file, written under a temporary name until the export is complete.
    /// </summary>
    private sealed class PartFile(SplitPart? part, string path)
    {
        private StreamWriter? _writer;

        public SplitPart? Part { get; } = part;

        public string Path { get; } = path;

        public string PartialPath => Path + PartialSuffix;

        public long Count { get; private set; }

        public void Open() =>
            _writer = new StreamWriter(PartialPath, append: false, Utf8NoBom, bufferSize: 1 << 16) { NewLine = "\n" };

        public async Task WriteAsync(string line, CancellationToken cancellationToken)
        {
            await _writer!.WriteLineAsync(line.AsMemory(), cancellationToken);
            Count++;
        }

        public async Task CloseAsync()
        {
            if (_writer is { } writer)
            {
                _writer = null;
                await writer.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Decides for each record, in list order, whether and how it is written. The order of the
    /// checks (unsupported, too long, copy) matches the preview counts.
    /// </summary>
    private sealed class Pipeline(ExportOptions options, PiiScanner? scanner)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public long Exported { get; private set; }

        public long Unsupported { get; private set; }

        public long OverLimit { get; private set; }

        public long Duplicates { get; private set; }

        public long Masked { get; private set; }

        public ExportLine? Convert(ExportRecord record)
        {
            RecordPayload payload;
            try
            {
                payload = PayloadJson.Deserialize(record.Json);
            }
            catch (JsonException)
            {
                Unsupported++; // unreadable stored content cannot be written in any format
                return null;
            }

            if (!ExportFormats.CanWrite(options.Format, payload))
            {
                Unsupported++;
                return null;
            }

            if (options.SkipOverContextLimit && record.IsOverLimit)
            {
                OverLimit++;
                return null;
            }

            if (options.SkipDuplicates && !_seen.Add(record.ContentHash))
            {
                Duplicates++;
                return null;
            }

            // The split follows the stored question, so masking never moves a record to another file.
            SplitPart? part = options.Split is { } split ? ExportSplit.Assign(split, ExportSplit.GroupKey(payload, record.Id)) : null;
            if (scanner is not null && scanner.Mask(payload) is { HasFindings: true } masked)
            {
                payload = masked.Payload;
                Masked++;
            }

            Exported++;
            return new ExportLine(part, ExportRenderer.Render(options.Format, payload));
        }
    }
}
