using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Persistence.Converters;
using AIDatasetStudio.Infrastructure.Records;
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
    public async Task A_file_from_the_first_schema_version_gets_previews_and_search()
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
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
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
    private static async Task<(Guid DatasetId, Guid RecordId)> CreateFirstVersionFileAsync(string path)
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
            INSERT INTO DatasetRecords (Id, DatasetId, Type, Status, Ordinal, DataPayload, ContentHash, MetadataJson,
                                        SourceKind, CreatedAt, UpdatedAt)
            VALUES ($r, $d, 1, 0, 1, $json, $hash, '{}', 0, 0, 0);
            """;
        command.Parameters.AddWithValue("$ws", SqliteValues.Guid(workspace.Id));
        command.Parameters.AddWithValue("$p", SqliteValues.Guid(project.Id));
        command.Parameters.AddWithValue("$d", SqliteValues.Guid(dataset.Id));
        command.Parameters.AddWithValue("$r", SqliteValues.Guid(record.Id));
        command.Parameters.AddWithValue("$json", PayloadJson.Serialize(payload));
        command.Parameters.AddWithValue("$hash", record.ContentHash);
        await command.ExecuteNonQueryAsync(Ct);
        SqliteConnection.ClearPool(connection);

        return (dataset.Id, record.Id);
    }
}
