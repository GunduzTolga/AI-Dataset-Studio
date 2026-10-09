namespace AIDatasetStudio.Application.Tags;

/// <summary>
/// A tag with how many records carry it.
/// </summary>
/// <param name="Shortcut">1 … 5 for Mod+1 … Mod+5; <c>null</c> without one.</param>
/// <param name="RecordCount">Records with the tag (in the dataset asked for, or the whole workspace).</param>
public sealed record TagInfo(Guid Id, string Name, string ColorHex, int? Shortcut, long RecordCount)
{
    /// <summary>
    /// "kategori" for "kategori:hukuk"; <c>null</c> without a group.
    /// </summary>
    public string? Group => Domain.Entities.Tag.GroupOf(Name);
}

/// <summary>
/// The tags of the open workspace (SDD §5.1, §6.2). Tags are shared by all datasets; adding them
/// to records is a record change (<see cref="Records.IRecordCommandService.SetTagAsync"/>).
/// </summary>
/// <remarks>
/// Invalid names and colors, and names already used (ignoring case and Turkish I/İ/ı), raise
/// <see cref="Domain.Common.DomainException"/>; unknown ids raise <see cref="Common.NotFoundException"/>.
/// </remarks>
public interface ITagService
{
    /// <summary>
    /// All tags by name, with how many records of <paramref name="datasetId"/> (or of the whole
    /// workspace) carry each.
    /// </summary>
    Task<IReadOnlyList<TagInfo>> ListAsync(Guid? datasetId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a tag with the next palette color and, while one is free, the next shortcut.
    /// </summary>
    Task<TagInfo> CreateAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames, recolors and sets the shortcut of a tag. A shortcut another tag has moves to this one.
    /// </summary>
    Task UpdateAsync(Guid tagId, string name, string colorHex, int? shortcut, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a tag and removes it from every record. Versions keep their tag ids, but a deleted
    /// tag is not brought back with them.
    /// </summary>
    /// <returns>The number of records it was removed from.</returns>
    Task<long> DeleteAsync(Guid tagId, CancellationToken cancellationToken = default);
}
