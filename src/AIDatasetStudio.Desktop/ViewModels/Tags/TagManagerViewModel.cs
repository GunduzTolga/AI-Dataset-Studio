using System.Collections.ObjectModel;
using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Tags;

/// <summary>
/// A shortcut choice of a tag: none, or Mod+1 … Mod+5.
/// </summary>
public sealed record ShortcutOption(int? Slot, string Label);

/// <summary>
/// A color of the tag palette, offered for one tag (<see cref="Command"/> gives it that color).
/// </summary>
public sealed record ColorOption(string Hex, System.Windows.Input.ICommand Command)
{
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(Hex)).ToImmutable();
}

/// <summary>
/// One tag in Tools › Tags: its color, name, group, how many records carry it and its shortcut.
/// Changes are saved at once.
/// </summary>
public sealed partial class TagRowViewModel : ViewModelBase
{
    private readonly TagManagerViewModel _manager;
    private readonly bool _ready;

    internal TagRowViewModel(TagViewModel tag, TagManagerViewModel manager, string countText)
    {
        Tag = tag;
        _manager = manager;
        CountText = countText;
        SelectedShortcut = manager.ShortcutOptions.First(option => option.Slot == tag.Shortcut);
        Colors = [.. Domain.Entities.Tag.Palette.Select(hex => new ColorOption(hex, SetColorCommand))];
        _ready = true;
    }

    public TagViewModel Tag { get; }

    public Guid Id => Tag.Id;

    public string Name => Tag.Name;

    public IBrush Color => Tag.Color;

    /// <summary>
    /// "kategori" for "kategori:hukuk": exports can be split within it.
    /// </summary>
    public string? Group => Tag.Info.Group;

    public bool HasGroup => Group is not null;

    /// <summary>
    /// The palette, for the color button.
    /// </summary>
    public IReadOnlyList<ColorOption> Colors { get; }

    /// <summary>
    /// "12 records" across all datasets.
    /// </summary>
    public string CountText { get; }

    [ObservableProperty]
    public partial ShortcutOption SelectedShortcut { get; set; }

    [RelayCommand]
    private Task SetColorAsync(string? hex) => hex is null ? Task.CompletedTask : _manager.UpdateAsync(this, Name, hex, Tag.Shortcut);

    [RelayCommand]
    private Task RenameAsync() => _manager.RenameAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _manager.DeleteAsync(this);

    [RelayCommand]
    private void ShowRecords() => _manager.ShowRecords(this);

    partial void OnSelectedShortcutChanged(ShortcutOption value)
    {
        if (_ready && value.Slot != Tag.Shortcut)
        {
            _ = _manager.UpdateAsync(this, Name, Tag.Info.ColorHex, value.Slot);
        }
    }
}

/// <summary>
/// Tools › Tags: create, rename, recolor and delete tags and give them Mod+1 … Mod+5 (SDD §6.2).
/// Tags belong to the workspace, so the counts cover all datasets. Changes are saved at once and
/// are not part of Edit › Undo.
/// </summary>
public sealed partial class TagManagerViewModel : ViewModelBase
{
    private readonly ITagService _tags;
    private readonly TagCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;

    public TagManagerViewModel(ITagService tags, TagCatalog catalog, IDialogService dialogs, Localizer localizer)
    {
        _tags = tags;
        _catalog = catalog;
        _dialogs = dialogs;
        _localizer = localizer;
        ShortcutOptions =
        [
            new ShortcutOption(null, localizer["Tags_NoShortcut"]),
            .. Enumerable.Range(1, Domain.Entities.Tag.MaxShortcut).Select(slot => new ShortcutOption(slot, TagCatalog.ShortcutText(slot))),
        ];
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<TagRowViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public IReadOnlyList<ShortcutOption> ShortcutOptions { get; }

    /// <summary>
    /// The name of a new tag ("kategori:hukuk" puts it in the group "kategori").
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasError => ErrorMessage is not null;

    public bool HasNotice => Notice is not null;

    /// <summary>
    /// A tag was deleted: records lost it, so lists filtered by tag must reload.
    /// </summary>
    public bool HasDeleted { get; private set; }

    /// <summary>
    /// The tag whose records the user asked to see when closing.
    /// </summary>
    public Guid? ShowTagRequested { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await _catalog.ReloadAsync();
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }

        Rebuild();
    }

    private bool CanAdd() => NewName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddAsync() => RunAsync(async () =>
    {
        var created = await _catalog.CreateAsync(NewName.Trim());
        NewName = string.Empty;
        Notice = _localizer.Format("Tags_Created", created.Name);
    });

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal Task UpdateAsync(TagRowViewModel row, string name, string colorHex, int? shortcut) => RunAsync(async () =>
    {
        await _tags.UpdateAsync(row.Id, name, colorHex, shortcut);
        await _catalog.ReloadAsync();
    });

    internal async Task RenameAsync(TagRowViewModel row)
    {
        var name = await _dialogs.PromptTextAsync(_localizer["Dialog_RenameTagTitle"], _localizer.Format("Dialog_RenameTagMessage", row.Name), row.Name);
        if (name is not null && name != row.Name)
        {
            await UpdateAsync(row, name, row.Tag.Info.ColorHex, row.Tag.Shortcut);
        }
    }

    internal async Task DeleteAsync(TagRowViewModel row)
    {
        if (!await _dialogs.ConfirmAsync(
                _localizer["Dialog_DeleteTagTitle"],
                _localizer.Format("Dialog_DeleteTagMessage", row.Name, row.Tag.Info.RecordCount),
                _localizer["Action_Delete"],
                isDestructive: true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var removed = await _tags.DeleteAsync(row.Id);
            HasDeleted = true;
            await _catalog.ReloadAsync();
            Notice = _localizer.Format("Tags_Deleted", row.Name, removed);
        });
    }

    internal void ShowRecords(TagRowViewModel row)
    {
        ShowTagRequested = row.Id;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // Saves a change, then shows the tags as they are now (a shortcut may have moved to another tag).
    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = null;
        Notice = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }

        Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (var tag in _catalog.Tags)
        {
            Items.Add(new TagRowViewModel(tag, this, _localizer.Format("Tags_RecordCount", tag.Info.RecordCount)));
        }
    }

    private string? Describe(Exception exception) =>
        UiErrors.TryDescribe(exception) is { } error ? _localizer.Format(error.Key, error.Args) : null;
}
