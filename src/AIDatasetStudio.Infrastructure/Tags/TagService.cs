using AIDatasetStudio.Application.Common;
using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIDatasetStudio.Infrastructure.Tags;

internal sealed class TagService(IWorkspaceDatabase database, TimeProvider timeProvider) : ITagService
{
    public async Task<IReadOnlyList<TagInfo>> ListAsync(Guid? datasetId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = datasetId is null
            ? """
              SELECT t.Id, t.Name, t.ColorHex, t.Shortcut, (SELECT COUNT(*) FROM RecordTags rt WHERE rt.TagId = t.Id)
              FROM Tags t ORDER BY t.NameKey;
              """
            : """
              SELECT t.Id, t.Name, t.ColorHex, t.Shortcut,
                     (SELECT COUNT(*) FROM RecordTags rt JOIN DatasetRecords r ON r.Id = rt.RecordId WHERE rt.TagId = t.Id AND r.DatasetId = $ds)
              FROM Tags t ORDER BY t.NameKey;
              """;
        if (datasetId is { } id)
        {
            command.Parameters.AddWithValue("$ds", SqliteValues.Guid(id));
        }

        var tags = new List<TagInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(new TagInfo(
                SqliteValues.ReadGuid(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetInt64(4)));
        }

        return tags;
    }

    public async Task<TagInfo> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        var existing = await context.Tags.AsNoTracking().ToListAsync(cancellationToken);
        // Colors go round the palette; shortcuts are handed out while one is free.
        var tag = Tag.Create(name, timeProvider.GetUtcNow(), Tag.Palette[existing.Count % Tag.Palette.Count]);
        EnsureUnique(existing, tag.NameKey, tag.Name, except: null);
        var taken = existing.Select(t => t.Shortcut).OfType<int>().ToHashSet();
        tag.AssignShortcut(Enumerable.Range(1, Tag.MaxShortcut).Select(slot => (int?)slot).FirstOrDefault(slot => !taken.Contains(slot!.Value)));

        context.Tags.Add(tag);
        await context.SaveChangesAsync(cancellationToken);
        return new TagInfo(tag.Id, tag.Name, tag.ColorHex, tag.Shortcut, 0);
    }

    public async Task UpdateAsync(Guid tagId, string name, string colorHex, int? shortcut, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var tags = await context.Tags.ToListAsync(cancellationToken);
        var tag = tags.SingleOrDefault(t => t.Id == tagId) ?? throw new NotFoundException(nameof(Tag), tagId);

        tag.Rename(name);
        EnsureUnique(tags, tag.NameKey, tag.Name, except: tagId);
        tag.Recolor(colorHex);
        if (shortcut != tag.Shortcut)
        {
            // The shortcut moves: free it first so the unique index never sees it twice.
            if (shortcut is not null && tags.SingleOrDefault(t => t.Id != tagId && t.Shortcut == shortcut) is { } holder)
            {
                holder.AssignShortcut(null);
                await context.SaveChangesAsync(cancellationToken);
            }

            tag.AssignShortcut(shortcut);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long> DeleteAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var context = database.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var tag = await context.Tags.SingleOrDefaultAsync(t => t.Id == tagId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tag), tagId);
        var count = await context.Records.LongCountAsync(r => r.Tags.Any(t => t.Id == tagId), cancellationToken);

        // RecordTags rows go with the tag (foreign-key cascade).
        context.Tags.Remove(tag);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    // NameKey is unique in the file; say so in words rather than as a constraint failure.
    private static void EnsureUnique(IEnumerable<Tag> tags, string nameKey, string name, Guid? except)
    {
        if (tags.Any(t => t.Id != except && t.NameKey == nameKey))
        {
            throw new DomainException($"A tag named '{name}' already exists.");
        }
    }
}
