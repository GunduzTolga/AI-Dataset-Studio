using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
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
/// Streams a dataset, or a frozen version of it, into JSON Lines files (SDD §10, §13, §14).
/// Records are read in their list order from one consistent snapshot, filtered, optionally
/// masked, converted and assigned to a split part; files are written next to their destination
/// and moved into place only when complete.
/// </summary>
internal sealed class ExportService(IWorkspaceDatabase database, IRecordAnalysisService analysis, TimeProvider timeProvider) : IExportService
{
    private const int BatchSize = 500;
    private const string PartialSuffix = ".partial";


    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ExportPreview> PreviewAsync(
        Guid datasetId,
        ExportOptions options,
        int sampleLines,
        Guid? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleLines);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var source = await ReadSourceAsync(connection, datasetId, versionId, cancellationToken);
        Validate(options, source.Type);
        if (options.Statuses.Count == 0)
        {
            return new ExportPreview(0, 0, 0, 0, 0, []);
        }

        // The checks in the pipeline's order; ROW_NUMBER finds the copies of an earlier record
        // among those not left out for a low score (a copy of a left-out record is written).
        var low = options.SkipLowJudgeScore ? "s.LowScore" : "0";
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SourceSql(source)},
            chosen AS (
                SELECT {UnsupportedSql(options, source.Type)} AS Unsupported,
                       {(options.SkipOverContextLimit ? "s.OverLimit" : "0")} AS OverLimit,
                       {low} AS LowScore,
                       {(options.SkipDuplicates ? $"ROW_NUMBER() OVER (PARTITION BY s.ContentHash, {low} ORDER BY s.Ordinal) > 1" : "0")} AS Copy,
                       {(options.SkipOverContextLimit ? "s.NotAnalyzed" : "0")} AS NotAnalyzed
                FROM s WHERE s.Status IN ({StatusList(options)}))
            SELECT COUNT(*),
                   COALESCE(SUM(Unsupported), 0),
                   COALESCE(SUM(NOT Unsupported AND OverLimit), 0),
                   COALESCE(SUM(NOT Unsupported AND NOT OverLimit AND NOT LowScore AND Copy), 0),
                   COALESCE(SUM(NotAnalyzed), 0),
                   COALESCE(SUM(NOT Unsupported AND NOT OverLimit AND LowScore), 0)
            FROM chosen;
            """;
        source.Bind(command);
        long selected, unsupported, overLimit, duplicates, notAnalyzed, lowScore;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            (selected, unsupported, overLimit, duplicates, notAnalyzed, lowScore) =
                (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        }

        var samples = new List<string>(sampleLines);
        if (sampleLines > 0)
        {
            var scanner = options.MaskPersonalData ? new PiiScanner(await analysis.GetCustomTermsAsync(cancellationToken)) : null;
            var pipeline = new Pipeline(options, scanner, parts: null);
            await foreach (var record in ReadAsync(connection, null, source, options, withTags: false, cancellationToken))
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

        return new ExportPreview(selected, unsupported, overLimit, duplicates, notAnalyzed, samples, lowScore);
    }

    public Task<ExportResult> ExportAsync(
        Guid datasetId,
        ExportOptions options,
        string destination,
        IProgress<ExportProgress>? progress = null,
        Guid? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        // Converting and masking is CPU work; keep it off the caller's (usually the UI) thread.
        return Task.Run(() => ExportCoreAsync(datasetId, versionId, options, Path.GetFullPath(destination), progress, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetSplitPartAsync(Guid datasetId, ExportOptions options, SplitPart part, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Split is not { } split || split.PercentOf(part) == 0)
        {
            return [];
        }

        Source source;
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        {
            source = await ReadSourceAsync(connection, datasetId, null, cancellationToken);
        }

        Validate(options, source.Type);

        // The same passes as an export, so the same records come out: token counts and groups of
        // near-duplicate questions first, then families and strata.
        await analysis.AnalyzeAsync(datasetId, cancellationToken: cancellationToken);
        var ids = new List<Guid>();
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        {
            await using var snapshot = connection.BeginTransaction(deferred: true);
            var families = await JoinFamiliesAsync(connection, snapshot, source, options, cancellationToken);
            var parts = split.IsStratified ? await StratifyAsync(connection, snapshot, source, options, split, families, cancellationToken) : null;
            var pipeline = new Pipeline(options, scanner: null, parts, families);
            await foreach (var record in ReadAsync(connection, snapshot, source, options, withTags: false, cancellationToken))
            {
                if (pipeline.PartOf(record) == part)
                {
                    ids.Add(record.Id);
                }
            }
        }

        return ids;
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
        return new ExportHistoryEntry(row.TargetFormat, row.RecordCount, [.. files.Select(f => f.Path)], row.ExportedAt, row.VersionNumber);
    }

    private async Task<ExportResult> ExportCoreAsync(
        Guid datasetId, Guid? versionId, ExportOptions options, string destination, IProgress<ExportProgress>? progress, CancellationToken cancellationToken)
    {
        Source source;
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        {
            source = await ReadSourceAsync(connection, datasetId, versionId, cancellationToken);
        }

        Validate(options, source.Type);

        // The length check needs every record's token count with the dataset's tokenizer, and the split
        // the groups of near-duplicate questions (quick when nothing changed); a version keeps its own.
        if (source.VersionId is null && (options.Split is not null || (options.SkipOverContextLimit && await analysis.CountPendingAsync(datasetId, cancellationToken) > 0)))
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

            Pipeline pipeline;
            await using (var connection = await database.OpenConnectionAsync(cancellationToken))
            {
                // One deferred read transaction: a consistent snapshot that does not block edits (WAL).
                await using var snapshot = connection.BeginTransaction(deferred: true);
                var families = options.Split is not null ? await JoinFamiliesAsync(connection, snapshot, source, options, cancellationToken) : null;
                var parts = options.Split is { IsStratified: true } stratified
                    ? await StratifyAsync(connection, snapshot, source, options, stratified, families, cancellationToken)
                    : null;
                pipeline = new Pipeline(options, scanner, parts, families);
                var total = await CountSelectedAsync(connection, snapshot, source, options, cancellationToken);
                long processed = 0;
                progress?.Report(new ExportProgress(0, total));
                await foreach (var record in ReadAsync(connection, snapshot, source, options, withTags: false, cancellationToken))
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
                pipeline.Masked,
                options.Training is { } training ? WriteTrainingFiles(training, source, options, files) : null,
                pipeline.LowJudgeScore);
            // The files are in place: record them even if cancellation comes now.
            await RememberAsync(source, options, result, CancellationToken.None);
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

    /// <summary>
    /// The fine-tuning configuration, next to the data files and naming them by file name, so the
    /// folder can be moved to a training machine as it is (M20).
    /// </summary>
    private static List<string> WriteTrainingFiles(TrainingOptions training, Source source, ExportOptions options, List<PartFile> files)
    {
        var folder = Path.GetDirectoryName(files[0].Path)!;
        var target = new TrainingTarget(
            source.Name, source.Type, options.Format, source.ContextLength,
            [.. files.Select(f => (f.Part, Path.GetFileName(f.Path)))], options.Split?.Seed ?? SplitOptions.DefaultSeed);
        var written = new List<string>();
        foreach (var config in TrainingConfigs.Render(training, target))
        {
            var path = Path.Combine(folder, config.FileName);
            var partial = path + ".partial";
            File.WriteAllText(partial, config.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(partial, path, overwrite: true);
            written.Add(path);
        }

        return written;
    }

    private async Task RememberAsync(Source source, ExportOptions options, ExportResult result, CancellationToken cancellationToken)
    {
        var datasetId = source.DatasetId;
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
            VersionId = source.VersionId,
            VersionNumber = source.VersionNumber,
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

        if (options.Training is { } training && !TrainingConfigs.Supports(training.Framework, options.Format, type))
        {
            throw new ArgumentException($"{training.Framework} cannot train on {options.Format} files.", nameof(options));
        }
    }

    private static async Task<Source> ReadSourceAsync(SqliteConnection connection, Guid datasetId, Guid? versionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = versionId is null
            ? "SELECT Type, NULL, Name, ContextLength FROM Datasets WHERE Id = $ds;"
            : "SELECT d.Type, v.Number, d.Name, v.ContextLength FROM DatasetVersions v JOIN Datasets d ON d.Id = v.DatasetId WHERE v.Id = $version AND v.DatasetId = $ds;";
        command.Parameters.AddWithValue("$ds", SqliteValues.Guid(datasetId));
        if (versionId is { } version)
        {
            command.Parameters.AddWithValue("$version", SqliteValues.Guid(version));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw versionId is { } id ? new NotFoundException(nameof(DatasetVersion), id) : new NotFoundException(nameof(Dataset), datasetId);
        }

        return new Source(
            (DatasetType)reader.GetInt64(0), datasetId, versionId, reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
    }

    private static async Task<long> CountSelectedAsync(
        SqliteConnection connection, SqliteTransaction transaction, Source source, ExportOptions options, CancellationToken cancellationToken)
    {
        if (options.Statuses.Count == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{SourceSql(source)} SELECT COUNT(*) FROM s WHERE s.Status IN ({StatusList(options)});";
        source.Bind(command);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// When variations are among the records that will be written, a first pass (same checks,
    /// nothing rendered) joins the groups of each family so they share a part (SDD §17.6);
    /// otherwise <c>null</c>.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>?> JoinFamiliesAsync(
        SqliteConnection connection, SqliteTransaction transaction, Source source, ExportOptions options, CancellationToken cancellationToken)
    {
        if (options.Statuses.Count == 0)
        {
            return null;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"{SourceSql(source)} SELECT EXISTS (SELECT 1 FROM s WHERE s.Status IN ({StatusList(options)}) AND s.Family IS NOT NULL);";
            source.Bind(command);
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0)
            {
                return null;
            }
        }

        var checks = new Pipeline(options, scanner: null, parts: null);
        var families = new SplitFamilies();
        await foreach (var record in ReadAsync(connection, transaction, source, options, withTags: false, cancellationToken))
        {
            if (checks.Accept(record) is { } payload)
            {
                families.Add(ExportSplit.GroupKey(payload, record.Id, record.NearGroup), record.Family ?? record.Id);
            }
        }

        return families.Build();
    }

    /// <summary>
    /// A first pass over the records that will be written (same checks, nothing rendered): their
    /// groups, strata and sizes, assigned to parts with exact shares per stratum (SDD §10.1).
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, SplitPart>> StratifyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Source source,
        ExportOptions options,
        SplitOptions split,
        IReadOnlyDictionary<string, string>? families,
        CancellationToken cancellationToken)
    {
        var checks = new Pipeline(options, scanner: null, parts: null, families);
        var groups = new Dictionary<string, (string Stratum, int Count)>(StringComparer.Ordinal);
        var order = new List<string>();
        await foreach (var record in ReadAsync(connection, transaction, source, options, withTags: true, cancellationToken))
        {
            if (checks.Accept(record) is not { } payload)
            {
                continue;
            }

            var key = checks.GroupKey(payload, record);
            if (groups.TryGetValue(key, out var group))
            {
                groups[key] = group with { Count = group.Count + 1 };
            }
            else
            {
                // A group is split within the stratum of its first record.
                var tags = JsonSerializer.Deserialize<List<string>>(record.TagNames ?? "[]") ?? [];
                groups[key] = (ExportSplit.StratumOf(split.StratifyBy, tags), 1);
                order.Add(key);
            }
        }

        return ExportSplit.AssignStratified(split, [.. order.Select(key => new StratifiedGroup(key, groups[key].Stratum, groups[key].Count))]);
    }

    // Records with a chosen status in list order, in batches, with whether they are over the context length.
    private static async IAsyncEnumerable<ExportRecord> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Source source,
        ExportOptions options,
        bool withTags,
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
                    {SourceSql(source)}
                    SELECT s.Id, s.Ordinal, s.ContentHash, s.DataPayload, s.OverLimit, {(withTags ? "s.TagNames" : "NULL")}, s.NearGroup, s.Family, s.LowScore
                    FROM s WHERE s.Status IN ({StatusList(options)}) AND s.Ordinal > $after
                    ORDER BY s.Ordinal
                    LIMIT {BatchSize};
                    """;
                source.Bind(command);
                command.Parameters.AddWithValue("$after", after);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    batch.Add(new ExportRecord(
                        SqliteValues.ReadGuid(reader.GetString(0)),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetBoolean(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : SqliteValues.ReadGuid(reader.GetString(6)),
                        reader.IsDBNull(7) || !Guid.TryParse(reader.GetString(7), out var family) ? null : family,
                        reader.GetBoolean(8)));
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

    /// <summary>
    /// The records to export as a common table <c>s</c> (Id, Ordinal, Status, ContentHash,
    /// DataPayload, OverLimit, NotAnalyzed, TagNames, NearGroup, Family, LowScore): the dataset's
    /// records with their up-to-date analysis, or a version's records with the token counts,
    /// context length and groups of near-duplicate questions frozen with it. Family is the record a
    /// variation was written from, as it is in the metadata (<see cref="VariantOf"/>). LowScore is
    /// a model judgment of 1–2 of exactly this content: a version's record keeps it while the
    /// judged content is the frozen one.
    /// </summary>
    private static string SourceSql(Source source) => source.VersionId is null
        ? $"""
          WITH s AS (
              SELECT r.Id AS Id, r.Ordinal AS Ordinal, r.Status AS Status, r.ContentHash AS ContentHash, r.DataPayload AS DataPayload,
                     (a.RecordId IS NOT NULL AND a.TotalTokens > d.ContextLength) AS OverLimit, (a.RecordId IS NULL) AS NotAnalyzed,
                     (SELECT json_group_array(t.Name) FROM RecordTags rt JOIN Tags t ON t.Id = rt.TagId WHERE rt.RecordId = r.Id) AS TagNames,
                     a.NearGroup AS NearGroup, {AnalysisSql.VariantOf("r")} AS Family, {LowScoreSql("r.Id", "r.ContentHash")} AS LowScore
              FROM DatasetRecords r JOIN Datasets d ON d.Id = r.DatasetId {AnalysisSql.FreshJoin}
              WHERE r.DatasetId = $ds)
          """
        : $"""
          WITH s AS (
              SELECT x.RecordId AS Id, m.Ordinal AS Ordinal, m.Status AS Status, x.ContentHash AS ContentHash, x.DataPayload AS DataPayload,
                     (m.Tokens IS NOT NULL AND m.Tokens > v.ContextLength) AS OverLimit, 0 AS NotAnalyzed,
                     (SELECT json_group_array(t.Name) FROM json_each(m.TagIds) j JOIN Tags t ON t.Id = j.value) AS TagNames,
                     m.NearGroup AS NearGroup, {AnalysisSql.VariantOf("x")} AS Family, {LowScoreSql("x.RecordId", "x.ContentHash")} AS LowScore
              FROM VersionRecordMappings m
              JOIN RecordRevisions x ON x.Id = m.RevisionId
              JOIN DatasetVersions v ON v.Id = m.VersionId
              WHERE m.VersionId = $version)
          """;

    private static string LowScoreSql(string recordId, string contentHash) =>
        $"EXISTS (SELECT 1 FROM RecordJudgments j WHERE j.RecordId = {recordId} AND j.ContentHash = {contentHash} AND j.Score <= {Judging.LowScore})";

    private static string StatusList(ExportOptions options) =>
        string.Join(", ", options.Statuses.Distinct().Select(s => ((int)s).ToString(CultureInfo.InvariantCulture)));

    // The SQL twin of the pipeline's format check, for the preview counts; tests keep them in step.
    private static string UnsupportedSql(ExportOptions options, DatasetType type) =>
        ExportFormats.IsLossy(options.Format, type)
            ? "(NOT (json_array_length(s.DataPayload, '$.messages') = 2 AND json_extract(s.DataPayload, '$.messages[0].role') = 'user' " +
              "AND json_extract(s.DataPayload, '$.messages[1].role') = 'assistant'))"
            : "0";

    /// <param name="VersionNumber">"1.2.0" for a version.</param>
    private sealed record Source(DatasetType Type, Guid DatasetId, Guid? VersionId, string? VersionNumber, string Name, int ContextLength)
    {
        public void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(DatasetId));
            if (VersionId is { } version)
            {
                command.Parameters.AddWithValue("$version", SqliteValues.Guid(version));
            }
        }
    }

    /// <param name="TagNames">The record's tag names as a JSON array, when asked for.</param>
    /// <param name="NearGroup">The first record of its group of near-duplicate questions, if any.</param>
    /// <param name="Family">The record it is a variation of, if any.</param>
    /// <param name="IsLowScore">The model scored this content 1–2.</param>
    private sealed record ExportRecord(
        Guid Id, string ContentHash, string Json, bool IsOverLimit, string? TagNames = null, Guid? NearGroup = null, Guid? Family = null, bool IsLowScore = false);

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
    /// checks (unsupported, too long, low model score, copy) matches the preview counts.
    /// </summary>
    /// <param name="parts">The parts of a stratified split by group key; <c>null</c> to assign by hash alone.</param>
    /// <param name="families">Group keys joined with those of their family (<see cref="SplitFamilies"/>), if any.</param>
    private sealed class Pipeline(
        ExportOptions options, PiiScanner? scanner, IReadOnlyDictionary<string, SplitPart>? parts, IReadOnlyDictionary<string, string>? families = null)
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public long Exported { get; private set; }

        public long Unsupported { get; private set; }

        public long OverLimit { get; private set; }

        public long LowJudgeScore { get; private set; }

        public long Duplicates { get; private set; }

        public long Masked { get; private set; }

        /// <summary>
        /// What keeps the record with related ones in the split: its group, joined with its family's.
        /// </summary>
        public string GroupKey(RecordPayload payload, ExportRecord record)
        {
            var key = ExportSplit.GroupKey(payload, record.Id, record.NearGroup);
            return families is not null && families.TryGetValue(key, out var joined) ? joined : key;
        }

        private SplitPart? Assign(RecordPayload payload, ExportRecord record) => options.Split is { } split
            ? parts is not null && parts.TryGetValue(GroupKey(payload, record), out var assigned)
                ? assigned
                : ExportSplit.Assign(split, GroupKey(payload, record))
            : null;

        public ExportLine? Convert(ExportRecord record)
        {
            if (Accept(record) is not { } payload)
            {
                return null;
            }

            // The split follows the stored question, so masking never moves a record to another file.
            var part = Assign(payload, record);
            if (scanner is not null && scanner.Mask(payload) is { HasFindings: true } masked)
            {
                payload = masked.Payload;
                Masked++;
            }

            Exported++;
            return new ExportLine(part, ExportRenderer.Render(options.Format, payload));
        }

        /// <summary>
        /// The checks and the split alone, nothing rendered: the part the record is written to, or
        /// <c>null</c> when it is not written or there is no split.
        /// </summary>
        public SplitPart? PartOf(ExportRecord record) => Accept(record) is { } payload ? Assign(payload, record) : null;

        /// <summary>
        /// The checks alone: the record's content if it is written, otherwise <c>null</c> (counted).
        /// </summary>
        public RecordPayload? Accept(ExportRecord record)
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

            if (options.SkipLowJudgeScore && record.IsLowScore)
            {
                LowJudgeScore++;
                return null;
            }

            if (options.SkipDuplicates && !_seen.Add(record.ContentHash))
            {
                Duplicates++;
                return null;
            }

            return payload;
        }
    }
}
