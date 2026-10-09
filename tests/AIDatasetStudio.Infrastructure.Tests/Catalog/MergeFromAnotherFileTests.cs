using System.Security.Cryptography;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Catalog;
using AIDatasetStudio.Infrastructure.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Tests.Catalog;

/// <summary>
/// Merging datasets from another workspace file (M23, ADR-045).
/// </summary>
public sealed class MergeFromAnotherFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Datasets_of_a_file_open_elsewhere_are_copied_with_their_tags_matched_by_name()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var project = await ws.Catalog.CreateProjectAsync("Destek", cancellationToken: Ct);
        var ourCargo = await ws.Tags.CreateAsync("Kargo", Ct);

        // The other file stays open, its last writes still in its write-ahead log.
        await using var theirs = await TestWorkspace.CreateAsync(Ct);
        var theirProject = await theirs.Catalog.CreateProjectAsync("Mağaza", cancellationToken: Ct);
        var faq = await theirs.Catalog.CreateDatasetAsync(theirProject, "Mağaza SSS", DatasetType.Instruction, Ct);
        await theirs.Catalog.SetAIGuidanceAsync(faq, "Kısa yaz.", Ct);
        var cargo = await theirs.Tags.CreateAsync("kargo", Ct);
        var returns = await theirs.Tags.CreateAsync("İade", Ct);
        await theirs.Tags.UpdateAsync(returns.Id, returns.Name, "#E11D48", shortcut: 2, Ct);
        await theirs.Tags.CreateAsync("kullanılmıyor", Ct);
        var root = await Add(theirs, faq, "Şifremi unuttum", "Bağlantıya tıklayın.");
        await theirs.Commands.SetStatusAsync([root], RecordStatus.Approved, Ct);
        await theirs.Commands.SetTagAsync([root], cargo.Id, present: true, Ct);
        await theirs.Commands.SetTagAsync([root], returns.Id, present: true, Ct);
        await theirs.Commands.AddManyAsync(faq, [new NewRecord(new InstructionPayload("Parolamı nasıl sıfırlarım?", "", "Bağlantıya tıklayın."), new RecordLineage(RecordSourceKind.Synthetic, GeneratedByModel: "qwen2.5:7b"), VariantOf.For((await theirs.Queries.GetAsync(root, Ct))!))], Ct);

        await using var source = await ws.Merge.OpenWorkspaceAsync(theirs.FilePath, Ct);
        Assert.Equal("test.adstudio", source.FileName);
        var dataset = Assert.Single(Assert.Single(source.Projects).Datasets);
        Assert.Equal(("Mağaza SSS", 2L), (dataset.Name, dataset.RecordCount));

        var result = await ws.Merge.MergeAsync(new DatasetMergeRequest([faq], project, "Mağaza SSS", From: source), cancellationToken: Ct);

        var merged = (await ws.Catalog.GetProjectsAsync(Ct)).Single().Datasets.Single(d => d.Id == result.DatasetId);
        Assert.Equal((2L, "Kısa yaz."), (merged.RecordCount, merged.AIGuidance));
        var copies = new List<RecordDetails>();
        foreach (var item in (await ws.Queries.GetPageAsync(result.DatasetId, RecordFilter.None, after: null, pageSize: 10, Ct)).Items)
        {
            copies.Add((await ws.Queries.GetAsync(item.Id, Ct))!);
        }

        Assert.Equal(RecordStatus.Approved, copies[0].Status);
        Assert.Equal(new VariantOf(copies[0].Id, 1), VariantOf.FromMetadata(copies[1].MetadataJson));

        // "kargo" is this workspace's "Kargo"; "İade" is added with its color and without its
        // shortcut; a tag no copied record carries is not added.
        var ourTags = await ws.Tags.ListAsync(cancellationToken: Ct);
        var addedReturns = Assert.Single(ourTags, t => t.Name == "İade");
        Assert.Equal(("#E11D48", (int?)null), (addedReturns.ColorHex, addedReturns.Shortcut));
        Assert.Equal(["Kargo", "İade"], ourTags.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { ourCargo.Id, addedReturns.Id }.Order(), copies[0].TagIds.Order());

        // The other file is as it was.
        Assert.Equal(2, (await theirs.Queries.GetPageAsync(faq, RecordFilter.None, after: null, pageSize: 10, Ct)).Items.Count);
        Assert.Equal(3, (await theirs.Tags.ListAsync(cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task A_read_only_file_is_never_written_and_its_copy_is_removed_once_done()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var project = await ws.Catalog.CreateProjectAsync("Destek", cancellationToken: Ct);
        var other = await TestWorkspace.CreateAsync(Ct);
        var dataset = await other.CreateDatasetAsync(cancellationToken: Ct);
        await Add(other, dataset, "Soru", "Cevap");
        var path = other.FilePath;
        await other.Workspace.CloseAsync(Ct);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path, Ct));

        string folder;
        await using (var source = await ws.Merge.OpenWorkspaceAsync(path, Ct))
        {
            // The copy is written (upgraded, its index repaired) even though the file cannot be.
            var copy = ((OtherWorkspace)source).CopyPath;
            Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
            folder = Path.GetDirectoryName(copy)!;
            await ws.Merge.MergeAsync(new DatasetMergeRequest([dataset], project, "Kopya", From: source), cancellationToken: Ct);
            Assert.True(Directory.Exists(folder));
        }

        Assert.False(Directory.Exists(folder));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path, Ct)));
        Assert.False(File.Exists(path + "-wal"));
        File.SetAttributes(path, FileAttributes.Normal);
        await other.DisposeAsync();
    }

    [Fact]
    public async Task A_stopped_merge_takes_back_the_tags_it_added()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var project = await ws.Catalog.CreateProjectAsync("Destek", cancellationToken: Ct);
        await using var theirs = await TestWorkspace.CreateAsync(Ct);
        var theirProject = await theirs.Catalog.CreateProjectAsync("Mağaza", cancellationToken: Ct);
        var a = await theirs.Catalog.CreateDatasetAsync(theirProject, "A", DatasetType.Instruction, Ct);
        var b = await theirs.Catalog.CreateDatasetAsync(theirProject, "B", DatasetType.Instruction, Ct);
        var tag = await theirs.Tags.CreateAsync("iade", Ct);
        await theirs.Commands.SetTagAsync([await Add(theirs, a, "İade süresi?", "14 gün.")], tag.Id, present: true, Ct);
        await Add(theirs, b, "Kargo?", "1-3 gün.");

        await using var source = await ws.Merge.OpenWorkspaceAsync(theirs.FilePath, Ct);
        using var cancelled = new CancellationTokenSource();
        var reports = 0;

        // Stopped after A is written: its dataset and the tag added for it go again.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ws.Merge.MergeAsync(
            new DatasetMergeRequest([a, b], project, "Yarım", From: source),
            new InlineProgress<DatasetMergeProgress>(_ =>
            {
                if (++reports == 2)
                {
                    cancelled.Cancel();
                }
            }),
            cancelled.Token));

        Assert.Empty((await ws.Catalog.GetProjectsAsync(Ct)).Single().Datasets);
        Assert.Empty(await ws.Tags.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task The_open_workspace_and_files_that_are_missing_or_not_workspaces_are_refused_by_their_own_names()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);

        var error = await Assert.ThrowsAsync<WorkspaceFileException>(() => ws.Merge.OpenWorkspaceAsync(ws.FilePath, Ct));
        Assert.Equal((WorkspaceFileError.AlreadyOpen, ws.FilePath), (error.Error, error.FilePath));

        var missing = Path.Combine(ws.Directory, "yok.adstudio");
        error = await Assert.ThrowsAsync<WorkspaceFileException>(() => ws.Merge.OpenWorkspaceAsync(missing, Ct));
        Assert.Equal((WorkspaceFileError.NotFound, missing), (error.Error, error.FilePath));

        var text = ws.WriteFile("notlar.adstudio", new string('x', 4096));
        error = await Assert.ThrowsAsync<WorkspaceFileException>(() => ws.Merge.OpenWorkspaceAsync(text, Ct));
        Assert.Equal((WorkspaceFileError.NotAWorkspace, text), (error.Error, error.FilePath));

        // Another program's database: the error names the chosen file, not its copy.
        var database = Path.Combine(ws.Directory, "baska.db");
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE notes (text TEXT);";
            await command.ExecuteNonQueryAsync(Ct);
        }

        error = await Assert.ThrowsAsync<WorkspaceFileException>(() => ws.Merge.OpenWorkspaceAsync(database, Ct));
        Assert.Equal((WorkspaceFileError.NotAWorkspace, database), (error.Error, error.FilePath));
        Assert.Contains(database, error.Message, StringComparison.Ordinal);

        // Saved by a newer version.
        var future = await TestWorkspace.CreateAsync(Ct);
        var futurePath = future.FilePath;
        await future.Workspace.CloseAsync(Ct);
        await using (var connection = new SqliteConnection($"Data Source={futurePath};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29990101000000_FromTheFuture', '99.0.0');";
            await command.ExecuteNonQueryAsync(Ct);
        }

        error = await Assert.ThrowsAsync<WorkspaceFileException>(() => ws.Merge.OpenWorkspaceAsync(futurePath, Ct));
        Assert.Equal((WorkspaceFileError.CreatedByNewerVersion, futurePath), (error.Error, error.FilePath));
        await future.DisposeAsync();
    }

    private static async Task<Guid> Add(TestWorkspace ws, Guid datasetId, string question, string answer) =>
        await ws.Commands.AddAsync(datasetId, new InstructionPayload(question, "", answer), RecordLineage.Manual(), Ct);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
