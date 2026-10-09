using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Infrastructure.Catalog;

/// <summary>
/// Merges datasets into a new one (M21, ADR-043) through the same snapshots that bring back
/// deleted records: every record is copied whole, with a new id, in the sources' order. The
/// sources are only read.
/// </summary>
internal sealed class DatasetMergeService(ICatalogService catalog, IRecordQueryService queries, IRecordCommandService commands) : IDatasetMergeService
{
    private const int BatchSize = 500;

    public async Task<DatasetMergeResult> MergeAsync(
        DatasetMergeRequest request, IProgress<DatasetMergeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceDatasetIds.Count == 0)
        {
            throw new ArgumentException("Choose at least one dataset to merge.", nameof(request));
        }

        var projects = await catalog.GetProjectsAsync(cancellationToken);
        if (projects.All(p => p.Id != request.TargetProjectId))
        {
            throw new NotFoundException(nameof(Project), request.TargetProjectId);
        }

        var datasets = projects.SelectMany(p => p.Datasets).ToDictionary(d => d.Id);
        var sources = request.SourceDatasetIds.Distinct().Select(id => datasets.GetValueOrDefault(id) ?? throw new NotFoundException(nameof(Dataset), id)).ToList();
        if (sources.Select(s => s.Type).Distinct().Count() > 1)
        {
            throw new ArgumentException("Only datasets of one type can be merged.", nameof(request));
        }

        var first = sources[0];
        var targetId = await catalog.CreateDatasetAsync(request.TargetProjectId, request.Name, first.Type, cancellationToken);
        try
        {
            await catalog.SetTokenSettingsAsync(targetId, first.Tokenizer, first.ContextLength, cancellationToken);
            await catalog.SetAIGuidanceAsync(targetId, first.AIGuidance, cancellationToken);
            return await CopyAsync(request, sources, targetId, progress, cancellationToken);
        }
        catch
        {
            // Cancelled or failed: no half-merged dataset stays behind.
            await catalog.DeleteDatasetAsync(targetId, CancellationToken.None);
            throw;
        }
    }

    private async Task<DatasetMergeResult> CopyAsync(
        DatasetMergeRequest request, List<DatasetSummary> sources, Guid targetId, IProgress<DatasetMergeProgress>? progress, CancellationToken cancellationToken)
    {
        // The records in order first, to tell how far the merge is.
        var order = new List<(Guid DatasetId, List<Guid> Ids)>();
        foreach (var source in sources)
        {
            var ids = new List<Guid>();
            RecordCursor? after = null;
            do
            {
                var page = await queries.GetPageAsync(source.Id, RecordFilter.None, after, BatchSize, cancellationToken);
                ids.AddRange(page.Items.Select(item => item.Id));

                after = page.Next;
            }
            while (after is not null);
            order.Add((source.Id, ids));
        }

        var total = order.Sum(o => (long)o.Ids.Count);
        // Each kept record's copy, by content and by the original's id; a variation is written
        // after the record it varies, so its family's copy is known by then.
        var kept = new Dictionary<string, VariantOf>(StringComparer.Ordinal);
        var families = new Dictionary<Guid, VariantOf>();
        long processed = 0, ordinal = 0, duplicates = 0, archived = 0;
        progress?.Report(new DatasetMergeProgress(0, total));
        foreach (var (_, ids) in order)
        {
            foreach (var batch in ids.Chunk(BatchSize))
            {
                var snapshots = (await queries.GetSnapshotsAsync(batch, cancellationToken)).OrderBy(s => s.Ordinal).ToList();
                var write = new List<RecordSnapshot>(snapshots.Count);
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.Status == RecordStatus.Archived && !request.IncludeArchived)
                    {
                        archived++;
                        continue;
                    }

                    var hash = snapshot.Payload.ComputeContentHash();
                    if (request.SkipDuplicates && kept.TryGetValue(hash, out var earlier))
                    {
                        // Variations of a left-out copy join the kept record's family.
                        families[snapshot.Id] = earlier;
                        duplicates++;
                        continue;
                    }

                    var copy = new VariantOf(Guid.CreateVersion7(), ++ordinal);
                    families[snapshot.Id] = copy;
                    kept.TryAdd(hash, copy);
                    write.Add(snapshot with
                    {
                        Id = copy.Id,
                        DatasetId = targetId,
                        Ordinal = ordinal,
                        Status = request.AsDrafts && snapshot.Status != RecordStatus.Archived ? RecordStatus.Draft : snapshot.Status,
                        MetadataJson = Family(snapshot.MetadataJson, families),
                    });
                }

                await commands.RestoreAsync(write, cancellationToken);
                processed += batch.Length;
                progress?.Report(new DatasetMergeProgress(processed, total));
            }
        }

        return new DatasetMergeResult(targetId, ordinal, duplicates, archived);
    }

    /// <summary>
    /// A variation's family is its first record's copy. A family whose first record was left out
    /// as archived ends: the variation stands on its own.
    /// </summary>
    private static string Family(string metadata, Dictionary<Guid, VariantOf> families) =>
        VariantOf.FromMetadata(metadata) is not { } family ? metadata
        : families.TryGetValue(family.Id, out var copy) ? VariantOf.WithFamily(metadata, copy)
        : VariantOf.WithoutFamily(metadata);
}
