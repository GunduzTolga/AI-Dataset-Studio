using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Catalog;

public sealed class CatalogServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_projects_and_datasets_in_creation_order_with_record_counts()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var hr = await ws.Catalog.CreateProjectAsync("HR Assistant", "Leave questions", Ct);
        ws.Clock.Now = ws.Clock.Now.AddMinutes(1);
        var support = await ws.Catalog.CreateProjectAsync("Support Bot", cancellationToken: Ct);
        var faq = await ws.Catalog.CreateDatasetAsync(hr, "Leave FAQ", DatasetType.Instruction, Ct);
        ws.Clock.Now = ws.Clock.Now.AddMinutes(1);
        var chats = await ws.Catalog.CreateDatasetAsync(hr, "Leave chats", DatasetType.Chat, Ct);
        await ws.Commands.AddAsync(faq, new InstructionPayload("q", "", "a"), RecordLineage.Manual(), Ct);
        await ws.Commands.AddAsync(faq, new InstructionPayload("q2", "", "a2"), RecordLineage.Manual(), Ct);

        var projects = await ws.Catalog.GetProjectsAsync(Ct);

        Assert.Equal([hr, support], projects.Select(p => p.Id));
        Assert.Equal("Leave questions", projects[0].Description);
        Assert.Equal([faq, chats], projects[0].Datasets.Select(d => d.Id));
        Assert.Equal([2L, 0L], projects[0].Datasets.Select(d => d.RecordCount));
        Assert.Equal(DatasetType.Chat, projects[0].Datasets[1].Type);
        Assert.Empty(projects[1].Datasets);
    }

    [Fact]
    public async Task Renames_projects_and_datasets()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        var projectId = (await ws.Catalog.GetProjectsAsync(Ct))[0].Id;

        await ws.Catalog.RenameProjectAsync(projectId, "  Renamed project ", Ct);
        await ws.Catalog.RenameDatasetAsync(datasetId, "Renamed dataset", Ct);

        var project = (await ws.Catalog.GetProjectsAsync(Ct))[0];
        Assert.Equal("Renamed project", project.Name);
        Assert.Equal("Renamed dataset", project.Datasets[0].Name);
    }

    [Fact]
    public async Task Invalid_names_and_unknown_ids_are_rejected()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);

        await Assert.ThrowsAsync<DomainException>(() => ws.Catalog.CreateProjectAsync(" ", cancellationToken: Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Catalog.RenameProjectAsync(Guid.NewGuid(), "x", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Catalog.CreateDatasetAsync(Guid.NewGuid(), "x", DatasetType.Chat, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Catalog.DeleteDatasetAsync(Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => ws.Catalog.DeleteProjectAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Deleting_a_dataset_removes_its_records_from_the_search_index()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await ws.Commands.AddAsync(datasetId, new InstructionPayload("şifre sıfırlama", "", ""), RecordLineage.Manual(), Ct);

        await ws.Catalog.DeleteDatasetAsync(datasetId, Ct);

        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM DatasetRecords"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordSearchKeys"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordSearch WHERE RecordSearch MATCH 'sifre'"));
    }

    [Fact]
    public async Task Deleting_a_project_removes_its_datasets_and_records()
    {
        await using var ws = await TestWorkspace.CreateAsync(Ct);
        var datasetId = await ws.CreateDatasetAsync(cancellationToken: Ct);
        await ws.Commands.AddAsync(datasetId, new InstructionPayload("kayıt", "", ""), RecordLineage.Manual(), Ct);
        var projectId = (await ws.Catalog.GetProjectsAsync(Ct))[0].Id;

        await ws.Catalog.DeleteProjectAsync(projectId, Ct);

        Assert.Empty(await ws.Catalog.GetProjectsAsync(Ct));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM Datasets"));
        Assert.Equal(0, await ws.ScalarAsync("SELECT COUNT(*) FROM RecordSearchKeys"));
        Assert.Equal(0, await ws.Queries.CountAsync(datasetId, RecordFilter.None, Ct));
    }
}
