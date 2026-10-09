using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Analysis;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using AIDatasetStudio.Infrastructure.Records;
using AIDatasetStudio.Infrastructure.Search;
using AIDatasetStudio.Infrastructure.Tests.Support;
using AIDatasetStudio.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AIDatasetStudio.Infrastructure.Tests.Workspaces;

/// <summary>
/// Files written by older versions must open, gain the new schema and have derived data filled in.
/// </summary>
public sealed class WorkspaceUpgradeTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"));

    public WorkspaceUpgradeTests() => Directory.CreateDirectory(_directory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_from_the_first_schema_version_gets_previews_search_and_analysis()
    {
        var path = Path.Combine(_directory, "old.adstudio");
        var (datasetId, recordId) = await CreateFirstVersionFileAsync(path);

        await using var service = new WorkspaceService(new FixedTimeProvider(Now));
        await service.OpenAsync(path, Ct);
        var queries = new RecordQueryService(service);

        var page = await queries.GetPageAsync(datasetId, RecordFilter.None, null, 10, Ct);
        Assert.Equal(recordId, Assert.Single(page.Items).Id);
        Assert.Equal("Yıllık izin hakkım?", page.Items[0].Preview);
        Assert.Equal(1, await queries.CountAsync(datasetId, new RecordFilter(SearchText: "izin"), Ct));

        await using var context = service.CreateDbContext();
        Assert.Empty(context.Database.GetPendingMigrations());

        // TagNameKey: existing tags get a key.
        Assert.Equal("destek", (await context.Tags.SingleAsync(Ct)).NameKey);

        // AddRecordAnalysis: old datasets get the default context length and can be analysed.
        Assert.Equal(Dataset.DefaultContextLength, (await context.Datasets.SingleAsync(Ct)).ContextLength);
        var analysis = new RecordAnalysisService(service, TestWorkspace.Tokenizers, new FixedTimeProvider(Now));
        Assert.Equal(1, await analysis.CountPendingAsync(datasetId, Ct));
        Assert.Equal(1, await analysis.AnalyzeAsync(datasetId, cancellationToken: Ct));
        Assert.Equal(0, await analysis.CountPendingAsync(datasetId, Ct));
    }

    [Fact]
    public async Task Search_repair_works_in_batches_and_indexes_unreadable_payloads_as_empty()
    {
        var path = Path.Combine(_directory, "old.adstudio");
        await CreateFirstVersionFileAsync(path, recordCount: 5);
        var connectionString = WorkspaceDatabase.BuildConnectionString(path, allowCreate: false);
        await using (var context = new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString)))
        {
            await context.Database.MigrateAsync(Ct);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE DatasetRecords SET DataPayload = '{' WHERE Ordinal = 3;";
            await corrupt.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(5, await SearchIndex.RepairAsync(connection, Ct, batchSize: 2));
        Assert.Equal(0, await SearchIndex.RepairAsync(connection, Ct, batchSize: 2));
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*), SUM(Preview <> '') FROM DatasetRecords r JOIN RecordSearchKeys k ON k.RecordId = r.Id;";
        await using var reader = await count.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        Assert.Equal((5L, 4L), (reader.GetInt64(0), reader.GetInt64(1)));
        SqliteConnection.ClearPool(connection);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        return ValueTask.CompletedTask;
    }

    // Builds a workspace exactly as the first released schema (InitialCreate) stored it.
    private static async Task<(Guid DatasetId, Guid RecordId)> CreateFirstVersionFileAsync(string path, int recordCount = 1)
    {
        var connectionString = WorkspaceDatabase.BuildConnectionString(path, allowCreate: true);
        await using (var context = new WorkspaceDbContext(WorkspaceDatabase.BuildOptions(connectionString)))
        {
            var firstMigration = context.Database.GetMigrations().First();
            await context.GetService<IMigrator>().MigrateAsync(firstMigration, Ct);
        }

        var workspace = Workspace.Create("Old", Now);
        var project = Project.Create("P", Now);
        var dataset = Dataset.Create(project.Id, "D", DatasetType.Instruction, Now);
        var payload = new InstructionPayload("Yıllık izin hakkım?", "", "14 gün.");
        var record = DatasetRecord.Create(dataset, payload, RecordLineage.Manual(), 1, Now);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            PRAGMA application_id = {{WorkspaceDatabase.ApplicationId}};
            INSERT INTO Workspace (Id, Name, CreatedAt) VALUES ($ws, 'Old', 0);
            INSERT INTO Projects (Id, Name, CreatedAt) VALUES ($p, 'P', 0);
            INSERT INTO Datasets (Id, ProjectId, Name, Type, DefaultTokenizer, CreatedAt) VALUES ($d, $p, 'D', 1, 'o200k_base', 0);
            INSERT INTO Tags (Id, Name, ColorHex) VALUES ($t, 'Destek', '#808080');
            INSERT INTO DatasetRecords (Id, DatasetId, Type, Status, Ordinal, DataPayload, ContentHash, MetadataJson,
                                        SourceKind, CreatedAt, UpdatedAt)
            VALUES ($r, $d, 1, 0, 1, $json, $hash, '{}', 0, 0, 0);
            """;
        // Further records: copies with their own ids and ordinals.
        for (var ordinal = 2; ordinal <= recordCount; ordinal++)
        {
            command.CommandText += $$"""

                INSERT INTO DatasetRecords (Id, DatasetId, Type, Status, Ordinal, DataPayload, ContentHash, MetadataJson,
                                            SourceKind, CreatedAt, UpdatedAt)
                VALUES ('{{SqliteValues.Guid(Guid.CreateVersion7())}}', $d, 1, 0, {{ordinal}}, $json, $hash, '{}', 0, 0, 0);
                """;
        }

        command.Parameters.AddWithValue("$ws", SqliteValues.Guid(workspace.Id));
        command.Parameters.AddWithValue("$p", SqliteValues.Guid(project.Id));
        command.Parameters.AddWithValue("$d", SqliteValues.Guid(dataset.Id));
        command.Parameters.AddWithValue("$r", SqliteValues.Guid(record.Id));
        command.Parameters.AddWithValue("$t", SqliteValues.Guid(Guid.CreateVersion7()));
        command.Parameters.AddWithValue("$json", PayloadJson.Serialize(payload));
        command.Parameters.AddWithValue("$hash", record.ContentHash);
        await command.ExecuteNonQueryAsync(Ct);
        SqliteConnection.ClearPool(connection);

        return (dataset.Id, record.Id);
    }
}
