using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Catalog;

internal sealed class CatalogService(IWorkspaceDatabase database, TimeProvider timeProvider, ITokenizerProvider tokenizers) : ICatalogService
{
    public async Task<IReadOnlyList<ProjectSummary>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();

        var projects = await context.Projects.AsNoTracking()
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var datasets = await context.Datasets.AsNoTracking()
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);
        var counts = await context.Records
            .GroupBy(r => r.DatasetId)
            .Select(g => new { DatasetId = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.DatasetId, x => x.Count, cancellationToken);

        var datasetsByProject = datasets.ToLookup(d => d.ProjectId);
        return projects
            .Select(p => new ProjectSummary(
                p.Id,
                p.Name,
                p.Description,
                datasetsByProject[p.Id]
                    .Select(d => new DatasetSummary(d.Id, d.ProjectId, d.Name, d.Type, counts.GetValueOrDefault(d.Id), d.DefaultTokenizer, d.ContextLength))
                    .ToList()))
            .ToList();
    }

    public async Task<Guid> CreateProjectAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        var project = Project.Create(name, timeProvider.GetUtcNow(), description);
        await using var context = database.CreateDbContext();
        context.Projects.Add(project);
        await context.SaveChangesAsync(cancellationToken);
        return project.Id;
    }

    public async Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var project = await context.Projects.FindAsync([projectId], cancellationToken)
            ?? throw new NotFoundException(nameof(Project), projectId);
        project.Rename(name);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        // Datasets and records go with it through ON DELETE CASCADE; a trigger cleans the search index.
        var deleted = await context.Projects.Where(p => p.Id == projectId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            throw new NotFoundException(nameof(Project), projectId);
        }
    }

    public async Task<Guid> CreateDatasetAsync(Guid projectId, string name, DatasetType type, CancellationToken cancellationToken = default)
    {
        var dataset = Dataset.Create(projectId, name, type, timeProvider.GetUtcNow());
        await using var context = database.CreateDbContext();
        if (!await context.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw new NotFoundException(nameof(Project), projectId);
        }

        context.Datasets.Add(dataset);
        await context.SaveChangesAsync(cancellationToken);
        return dataset.Id;
    }

    public async Task RenameDatasetAsync(Guid datasetId, string name, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var dataset = await context.Datasets.FindAsync([datasetId], cancellationToken)
            ?? throw new NotFoundException(nameof(Dataset), datasetId);
        dataset.Rename(name);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetTokenSettingsAsync(Guid datasetId, string tokenizer, int contextLength, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var dataset = await context.Datasets.FindAsync([datasetId], cancellationToken)
            ?? throw new NotFoundException(nameof(Dataset), datasetId);
        if (!tokenizers.Available.Contains(tokenizer, StringComparer.Ordinal))
        {
            throw new DomainException($"Unknown tokenizer '{tokenizer}'.");
        }

        // Results computed with another tokenizer no longer match the dataset and are recomputed.
        dataset.ChangeTokenSettings(tokenizer, contextLength);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var deleted = await context.Datasets.Where(d => d.Id == datasetId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            throw new NotFoundException(nameof(Dataset), datasetId);
        }
    }
}
