using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Persistence;
using AIDatasetStudio.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Tests.Workspaces;

public sealed class WorkspaceServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aids-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly WorkspaceService _service;

    public WorkspaceServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _service = new WorkspaceService(_clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_makes_a_wal_mode_workspace_file_and_opens_it()
    {
        var path = PathFor("new");
        var changed = 0;
        _service.CurrentChanged += (_, _) => changed++;

        var info = await _service.CreateAsync(path, "  Support Bot  ", cancellationToken: Ct);

        Assert.True(File.Exists(path));
        Assert.Equal("Support Bot", info.Name);
        Assert.Equal(Path.GetFullPath(path), info.FilePath);
        Assert.Equal(Now, info.CreatedAt);
        Assert.Same(info, _service.Current);
        Assert.Equal(1, changed);

        await _service.CloseAsync(Ct);
        Assert.Equal(WorkspaceDatabase.ApplicationId, await ScalarAsync(path, "PRAGMA application_id;"));
        Assert.Equal("wal", await ScalarAsync(path, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task Create_refuses_to_replace_an_existing_file_unless_asked()
    {
        var path = PathFor("existing");
        await File.WriteAllTextAsync(path, "keep me", Ct);

        var error = await Assert.ThrowsAsync<WorkspaceFileException>(() => _service.CreateAsync(path, "x", cancellationToken: Ct));

        Assert.Equal(WorkspaceFileError.AlreadyExists, error.Error);
        Assert.Equal("keep me", await File.ReadAllTextAsync(path, Ct));
        Assert.Null(_service.Current);
    }

    [Fact]
    public async Task Create_with_overwrite_replaces_the_currently_open_file()
    {
        var path = PathFor("replace");
        var first = await _service.CreateAsync(path, "First", cancellationToken: Ct);

        var second = await _service.CreateAsync(path, "Second", overwrite: true, cancellationToken: Ct);

        Assert.NotEqual(first.Id, second.Id);
        await _service.CloseAsync(Ct);
        var reopened = await _service.OpenAsync(path, Ct);
        Assert.Equal("Second", reopened.Name);
    }

    [Fact]
    public async Task Close_leaves_a_single_self_contained_file()
    {
        var path = PathFor("portable");
        await _service.CreateAsync(path, "Portable", cancellationToken: Ct);
        await AddSampleDataAsync(Ct);
        Assert.True(File.Exists(path + "-wal"));

        await _service.CloseAsync(Ct);

        Assert.Null(_service.Current);
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public async Task Data_survives_close_and_reopen()
    {
        var path = PathFor("roundtrip");
        var created = await _service.CreateAsync(path, "Round Trip", cancellationToken: Ct);
        var (datasetId, payload) = await AddSampleDataAsync(Ct);
        await _service.CloseAsync(Ct);

        // Copy the closed file elsewhere to prove it is self-contained.
        var copy = PathFor("roundtrip-copy");
        File.Copy(path, copy);
        var reopened = await _service.OpenAsync(copy, Ct);

        Assert.Equal(created.Id, reopened.Id);
        Assert.Equal("Round Trip", reopened.Name);

        await using var context = _service.CreateDbContext();
        var record = await context.Records.Include(r => r.Tags).SingleAsync(r => r.DatasetId == datasetId, Ct);
        Assert.Equal(payload, record.Payload);
        Assert.Equal(payload.ComputeContentHash(), record.ContentHash);
        Assert.Equal(RecordStatus.Approved, record.Status);
        Assert.Equal(new RecordLineage(RecordSourceKind.FileImport, "faq.jsonl#line=3", "ayse", "gpt-4o"), record.Lineage);
        Assert.Equal(Now, record.CreatedAt);
        Assert.Equal(["finance"], record.Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task Open_missing_file_reports_not_found()
    {
        var error = await Assert.ThrowsAsync<WorkspaceFileException>(() => _service.OpenAsync(PathFor("missing"), Ct));

        Assert.Equal(WorkspaceFileError.NotFound, error.Error);
    }

    [Fact]
    public async Task Open_rejects_a_foreign_sqlite_database()
    {
        var path = PathFor("foreign");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Things (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var error = await Assert.ThrowsAsync<WorkspaceFileException>(() => _service.OpenAsync(path, Ct));

        Assert.Equal(WorkspaceFileError.NotAWorkspace, error.Error);
        Assert.Null(_service.Current);
    }

    [Fact]
    public async Task Failed_open_keeps_the_current_workspace_open()
    {
        var current = await _service.CreateAsync(PathFor("current"), "Current", cancellationToken: Ct);
        var invalid = PathFor("invalid");
        await File.WriteAllTextAsync(invalid, new string('x', 4096), Ct);

        await Assert.ThrowsAsync<WorkspaceFileException>(() => _service.OpenAsync(invalid, Ct));

        Assert.Same(current, _service.Current);
        await using var context = _service.CreateDbContext();
        Assert.Equal("Current", (await context.Workspace.SingleAsync(Ct)).Name);
    }

    [Fact]
    public async Task Open_rejects_a_file_that_is_not_a_database()
    {
        var path = PathFor("text");
        await File.WriteAllTextAsync(path, new string('x', 4096), Ct);

        var error = await Assert.ThrowsAsync<WorkspaceFileException>(() => _service.OpenAsync(path, Ct));

        Assert.Equal(WorkspaceFileError.NotAWorkspace, error.Error);
    }

    [Fact]
    public async Task Open_refuses_files_written_by_a_newer_version()
    {
        var path = PathFor("future");
        await _service.CreateAsync(path, "Future", cancellationToken: Ct);
        await _service.CloseAsync(Ct);
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FromTheFuture', '99.0.0');";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var error = await Assert.ThrowsAsync<WorkspaceFileException>(() => _service.OpenAsync(path, Ct));

        Assert.Equal(WorkspaceFileError.CreatedByNewerVersion, error.Error);
    }

    [Fact]
    public void CreateDbContext_requires_an_open_workspace()
    {
        Assert.Throws<InvalidOperationException>(() => _service.CreateDbContext());
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp folder is cleaned eventually.
        }
    }

    private string PathFor(string name) => Path.Combine(_directory, name + WorkspaceFile.Extension);

    private async Task<(Guid DatasetId, RecordPayload Payload)> AddSampleDataAsync(CancellationToken cancellationToken)
    {
        await using var context = _service.CreateDbContext();
        var project = Project.Create("HR Assistant", Now);
        var dataset = Dataset.Create(project.Id, "Leave FAQ", DatasetType.Chat, Now);
        var tag = Tag.Create("finance", Now, "#22aa55");
        var payload = new ChatPayload("Sen kurumsal bir İK asistanısın.", [
            new ChatMessage(ChatRole.User, "Yıllık izin hakkım ne zaman başlar?"),
            new ChatMessage(ChatRole.Assistant, "1 yıllık kıdeminizi doldurduğunuzda."),
        ]);
        var record = DatasetRecord.Create(
            dataset,
            payload,
            new RecordLineage(RecordSourceKind.FileImport, "faq.jsonl#line=3", "ayse", "gpt-4o"),
            ordinal: 1,
            Now);
        record.ChangeStatus(RecordStatus.Approved, Now);
        record.AddTag(tag, Now);

        context.AddRange(project, dataset, tag, record);
        await context.SaveChangesAsync(cancellationToken);
        return (dataset.Id, payload);
    }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is long number ? (int)number : result;
    }
}
