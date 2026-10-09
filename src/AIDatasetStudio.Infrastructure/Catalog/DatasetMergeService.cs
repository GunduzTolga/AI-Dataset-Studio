using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Infrastructure.Catalog;

/// <summary>
/// Merges datasets into a new one (M21, ADR-043) through the same snapshots that bring back
/// deleted records: every record is copied whole, with a new id, in the sources' order. The
/// sources are only read; they may be in another workspace file (M23), whose tags are matched to
/// this workspace's by name.
/// </summary>
internal sealed class DatasetMergeService(
    ICatalogService catalog,
    IRecordQueryService queries,
    IRecordCommandService commands,
    IWorkspaceService workspaces,
    ITagService tags,
    ITokenizerProvider tokenizers,
    TimeProvider timeProvider) : IDatasetMergeService
{
    private const int BatchSize = 500;

    public Task<DatasetMergeResult> MergeAsync(
        DatasetMergeRequest request, IProgress<DatasetMergeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceDatasetIds.Count == 0)
        {
            throw new ArgumentException("Choose at least one dataset to merge.", nameof(request));
        }

        // SQLite's async calls complete synchronously; keep the work off the caller's (UI) thread
        // so progress shows and the merge can be stopped.
        return Task.Run(() => MergeCoreAsync(request, progress, cancellationToken), cancellationToken);
    }

    public async Task<IMergeSource> OpenWorkspaceAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (workspaces.Current is { } current && SamePath(current.FilePath, path))
        {
            throw new WorkspaceFileException(
                WorkspaceFileError.AlreadyOpen, current.FilePath, "This is the open workspace; its datasets are merged without opening it again.");
        }

        // Copying, upgrading the copy and repairing its search index can take a while: off the UI thread.
        return await Task.Run(async () => await OtherWorkspace.OpenAsync(path, timeProvider, tokenizers, cancellationToken), cancellationToken);
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private async Task<DatasetMergeResult> MergeCoreAsync(
        DatasetMergeRequest request, IProgress<DatasetMergeProgress>? progress, CancellationToken cancellationToken)
    {
        var other = request.From switch
        {
            null => null,
            OtherWorkspace workspace => workspace,
            _ => throw new ArgumentException("Merge sources come from OpenWorkspaceAsync.", nameof(request)),
        };
        var projects = await catalog.GetProjectsAsync(cancellationToken);
        if (projects.All(p => p.Id != request.TargetProjectId))
        {
            throw new NotFoundException(nameof(Project), request.TargetProjectId);
        }

        var datasets = (other?.Projects ?? projects).SelectMany(p => p.Datasets).ToDictionary(d => d.Id);
        var sources = request.SourceDatasetIds.Distinct().Select(id => datasets.GetValueOrDefault(id) ?? throw new NotFoundException(nameof(Dataset), id)).ToList();
        if (sources.Select(s => s.Type).Distinct().Count() > 1)
        {
            throw new ArgumentException("Only datasets of one type can be merged.", nameof(request));
        }

        var first = sources[0];
        var targetId = await catalog.CreateDatasetAsync(request.TargetProjectId, request.Name, first.Type, cancellationToken);
        TagMap? tagMap = null;
        try
        {
            if (other is not null)
            {
                tagMap = new TagMap(await other.Tags.ListAsync(cancellationToken: cancellationToken), tags);
            }

            await catalog.SetTokenSettingsAsync(targetId, first.Tokenizer, first.ContextLength, cancellationToken);
            await catalog.SetAIGuidanceAsync(targetId, first.AIGuidance, cancellationToken);
            return await CopyAsync(request, other?.Queries ?? queries, tagMap, sources, targetId, progress, cancellationToken);
        }
        catch
        {
            // Cancelled or failed: no half-merged dataset, nor the tags added for it, stays behind.
            await catalog.DeleteDatasetAsync(targetId, CancellationToken.None);
            foreach (var added in tagMap?.Added ?? [])
            {
                await tags.DeleteAsync(added, CancellationToken.None);
            }

            throw;
        }
    }

    /// <param name="sourceQueries">Where the sources are read: this workspace, or the other file's copy.</param>
    /// <param name="tagMap">The other file's tags as this workspace's; <c>null</c> within this workspace.</param>
    private async Task<DatasetMergeResult> CopyAsync(
        DatasetMergeRequest request,
        IRecordQueryService sourceQueries,
        TagMap? tagMap,
        List<DatasetSummary> sources,
        Guid targetId,
        IProgress<DatasetMergeProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The records in order first, to tell how far the merge is.
        var order = new List<(Guid DatasetId, List<Guid> Ids)>();
        foreach (var source in sources)
        {
            var ids = new List<Guid>();
            RecordCursor? after = null;
            do
            {
                var page = await sourceQueries.GetPageAsync(source.Id, RecordFilter.None, after, BatchSize, cancellationToken);
                ids.AddRange(page.Items.Select(item => item.Id));

                after = page.Next;
            }
            while (after is not null);
            order.Add((source.Id, ids));
        }

        var total = order.Sum(o => (long)o.Ids.Count);
        // By content, the family each kept record belongs to (its first record's copy), for the
        // copies left out of it; archived ones apart, so an archived record never leaves out a
        // live one. By the original's id, the family to join; a variation is written after the
        // record it varies, so its family's copy is known by then.
        var kept = new Dictionary<string, VariantOf>(StringComparer.Ordinal);
        var keptArchived = new Dictionary<string, VariantOf>(StringComparer.Ordinal);
        var families = new Dictionary<Guid, VariantOf>();
        long processed = 0, ordinal = 0, duplicates = 0, archived = 0;
        progress?.Report(new DatasetMergeProgress(0, total));
        foreach (var (_, ids) in order)
        {
            foreach (var batch in ids.Chunk(BatchSize))
            {
                var snapshots = (await sourceQueries.GetSnapshotsAsync(batch, cancellationToken)).OrderBy(s => s.Ordinal).ToList();
                var write = new List<RecordSnapshot>(snapshots.Count);
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.Status == RecordStatus.Archived && !request.IncludeArchived)
                    {
                        archived++;
                        continue;
                    }

                    var hash = snapshot.Payload.ComputeContentHash();
                    var isArchived = snapshot.Status == RecordStatus.Archived;
                    if (request.SkipDuplicates && (kept.TryGetValue(hash, out var earlier) || (isArchived && keptArchived.TryGetValue(hash, out earlier))))
                    {
                        // Variations of a left-out copy join the kept record's family.
                        families[snapshot.Id] = earlier;
                        duplicates++;
                        continue;
                    }

                    var copy = new VariantOf(Guid.CreateVersion7(), ++ordinal);
                    families[snapshot.Id] = copy;
                    var metadata = Family(snapshot.MetadataJson, families);
                    (isArchived ? keptArchived : kept).TryAdd(hash, VariantOf.FromMetadata(metadata) ?? copy);
                    write.Add(snapshot with
                    {
                        Id = copy.Id,
                        DatasetId = targetId,
                        Ordinal = ordinal,
                        Status = request.AsDrafts && !isArchived ? RecordStatus.Draft : snapshot.Status,
                        MetadataJson = metadata,
                        TagIds = tagMap is null ? snapshot.TagIds : await tagMap.MapAsync(snapshot.TagIds, cancellationToken),
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

    /// <summary>
    /// The other workspace's tags as this workspace's, by name. A tag only it has is added when a
    /// copied record first carries it, with its color and no shortcut (the keys 1–5 stay with the
    /// tags they are on).
    /// </summary>
    private sealed class TagMap(IReadOnlyList<TagInfo> theirs, ITagService tags)
    {
        private readonly Dictionary<Guid, TagInfo> _theirs = theirs.ToDictionary(t => t.Id);
        private readonly Dictionary<Guid, Guid> _map = [];
        private Dictionary<string, Guid>? _ours;

        /// <summary>
        /// The tags added to this workspace for the merge.
        /// </summary>
        public List<Guid> Added { get; } = [];

        public async Task<IReadOnlyList<Guid>> MapAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
        {
            var mapped = new List<Guid>(ids.Count);
            foreach (var id in ids)
            {
                if (!_map.TryGetValue(id, out var mine))
                {
                    if (!_theirs.TryGetValue(id, out var tag))
                    {
                        continue;
                    }

                    _ours ??= await OursAsync(cancellationToken);
                    if (!_ours.TryGetValue(Tag.FoldName(tag.Name), out mine))
                    {
                        var added = await tags.CreateAsync(tag.Name, cancellationToken);
                        Added.Add(added.Id);
                        await tags.UpdateAsync(added.Id, added.Name, tag.ColorHex, shortcut: null, cancellationToken);
                        mine = _ours[Tag.FoldName(tag.Name)] = added.Id;
                    }

                    _map[id] = mine;
                }

                mapped.Add(mine);
            }

            return mapped;
        }

        // Files made before tag names were folded may hold "İade" and "iade"; the first one wins.
        private async Task<Dictionary<string, Guid>> OursAsync(CancellationToken cancellationToken)
        {
            var ours = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var tag in await tags.ListAsync(cancellationToken: cancellationToken))
            {
                ours.TryAdd(Tag.FoldName(tag.Name), tag.Id);
            }

            return ours;
        }
    }
}
