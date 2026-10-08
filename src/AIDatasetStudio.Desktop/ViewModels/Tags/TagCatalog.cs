using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.Tags;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.ViewModels.Tags;

/// <summary>
/// A tag as the UI shows it: a colored dot and its name, and its Mod+N shortcut.
/// </summary>
public sealed class TagViewModel(TagInfo tag, string shortcutText)
{
    public Guid Id => Info.Id;

    public TagInfo Info { get; } = tag;

    public string Name => Info.Name;

    public IBrush Color { get; } = new SolidColorBrush(Avalonia.Media.Color.Parse(tag.ColorHex)).ToImmutable();

    public int? Shortcut => Info.Shortcut;

    /// <summary>
    /// "Ctrl+1" (or "⌘1"); empty without a shortcut.
    /// </summary>
    public string ShortcutText { get; } = shortcutText;

    public bool HasShortcut => Info.Shortcut is not null;

    /// <summary>
    /// Mod+N, shown next to the tag in menus; <c>null</c> without a shortcut.
    /// </summary>
    public KeyGesture? Gesture => Info.Shortcut is { } slot ? TagCatalog.GestureFor(slot) : null;

    public override string ToString() => Name;
}

/// <summary>
/// A tag of the selected records with how many of them carry it.
/// </summary>
public sealed record TagCountItem(TagViewModel Tag, int Count)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// The tags of the open workspace, kept for the editor, the list, filters and shortcuts. Reloaded
/// whenever tags are created, changed or deleted; <see cref="Changed"/> tells the views.
/// </summary>
public sealed partial class TagCatalog(ITagService tags) : ObservableObject
{
    private Dictionary<Guid, TagViewModel> _byId = [];

    public event EventHandler? Changed;

    /// <summary>
    /// All tags by name.
    /// </summary>
    public ObservableCollection<TagViewModel> Tags { get; } = [];

    /// <summary>
    /// The tags with a shortcut, by shortcut.
    /// </summary>
    public IReadOnlyList<TagViewModel> WithShortcuts => [.. Tags.Where(t => t.HasShortcut).OrderBy(t => t.Shortcut)];

    public bool IsEmpty => Tags.Count == 0;

    public async Task ReloadAsync()
    {
        var list = await tags.ListAsync();
        Tags.Clear();
        foreach (var tag in list)
        {
            Tags.Add(new TagViewModel(tag, tag.Shortcut is { } slot ? ShortcutText(slot) : string.Empty));
        }

        _byId = Tags.ToDictionary(t => t.Id);
        OnPropertyChanged(nameof(WithShortcuts));
        OnPropertyChanged(nameof(IsEmpty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forgets the tags when the workspace closes.
    /// </summary>
    public void Clear()
    {
        Tags.Clear();
        _byId = [];
        OnPropertyChanged(nameof(WithShortcuts));
        OnPropertyChanged(nameof(IsEmpty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TagViewModel? Find(Guid id) => _byId.GetValueOrDefault(id);

    public TagViewModel? ForShortcut(int slot) => Tags.FirstOrDefault(t => t.Shortcut == slot);

    /// <summary>
    /// The known tags among <paramref name="ids"/>, by name; unknown ids are left out.
    /// </summary>
    public IReadOnlyList<TagViewModel> Resolve(IEnumerable<Guid> ids) =>
        [.. ids.Select(Find).OfType<TagViewModel>().OrderBy(t => Domain.Entities.Tag.FoldName(t.Name), StringComparer.Ordinal)];

    /// <summary>
    /// A tag by name (ignoring case and Turkish I/İ/ı), or <c>null</c>.
    /// </summary>
    public TagViewModel? FindByName(string name) =>
        Tags.FirstOrDefault(t => Domain.Entities.Tag.FoldName(t.Name) == Domain.Entities.Tag.FoldName(name.Trim()));

    /// <summary>
    /// Creates a tag (with the next color and a free shortcut) and reloads.
    /// </summary>
    public async Task<TagViewModel> CreateAsync(string name)
    {
        var created = await tags.CreateAsync(name);
        await ReloadAsync();
        return Find(created.Id)!;
    }

    /// <summary>
    /// "Ctrl+1" on Windows and Linux, "⌘1" on macOS.
    /// </summary>
    public static string ShortcutText(int slot) => GestureFor(slot).ToString("p", null);

    /// <summary>
    /// Mod+<paramref name="slot"/> (the digit row).
    /// </summary>
    public static KeyGesture GestureFor(int slot) => new(Key.D0 + slot, CommandModifiers);

    public static KeyModifiers CommandModifiers =>
        Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
}
