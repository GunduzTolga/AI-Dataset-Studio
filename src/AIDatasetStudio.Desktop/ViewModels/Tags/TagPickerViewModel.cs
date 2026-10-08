using AIDatasetStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Tags;

/// <summary>
/// "Add tag": a box that narrows the tags as you type and creates a new tag when none has that
/// name. It stays open after a pick so several tags can be added in a row.
/// </summary>
public sealed partial class TagPickerViewModel : ViewModelBase
{
    private readonly TagCatalog _catalog;
    private readonly Localizer _localizer;
    private readonly StatusBarViewModel _status;
    private readonly Func<TagViewModel, Task> _pick;
    private Func<IReadOnlyCollection<Guid>> _excluded = () => [];

    /// <param name="catalog">The workspace's tags.</param>
    /// <param name="localizer">For the "create" label.</param>
    /// <param name="status">Where a name that cannot be used is reported.</param>
    /// <param name="pick">Adds the chosen (or created) tag.</param>
    public TagPickerViewModel(TagCatalog catalog, Localizer localizer, StatusBarViewModel status, Func<TagViewModel, Task> pick)
    {
        _catalog = catalog;
        _localizer = localizer;
        _status = status;
        _pick = pick;
        _catalog.Changed += (_, _) => Refresh();
        _localizer.CultureChanged += (_, _) => OnPropertyChanged(nameof(CreateText));
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateText))]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>
    /// The tags not yet added whose name contains the query, by name.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatches))]
    public partial IReadOnlyList<TagViewModel> Matches { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool CanCreate { get; private set; }

    public bool HasMatches => Matches.Count > 0;

    /// <summary>
    /// Nothing to pick and nothing typed: say how tags are made.
    /// </summary>
    public bool IsEmpty => !HasMatches && !CanCreate;

    public string CreateText => _localizer.Format("TagPicker_Create", Query.Trim());

    /// <summary>
    /// Opens the picker; <paramref name="excluded"/> gives the tags already added.
    /// </summary>
    public void Open(Func<IReadOnlyCollection<Guid>> excluded)
    {
        _excluded = excluded;
        Query = string.Empty;
        Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private async Task PickAsync(TagViewModel? tag)
    {
        if (tag is null)
        {
            return;
        }

        await _pick(tag);
        Query = string.Empty;
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var name = Query.Trim();
        if (name.Length == 0 || _catalog.FindByName(name) is not null)
        {
            return;
        }

        TagViewModel created;
        try
        {
            created = await _catalog.CreateAsync(name);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
            return;
        }

        await PickAsync(created);
    }

    /// <summary>
    /// Enter: the tag with exactly that name, else the only match, else a new tag.
    /// </summary>
    [RelayCommand]
    private Task ConfirmAsync()
    {
        var name = Query.Trim();
        if (name.Length == 0)
        {
            return Matches.Count == 1 ? PickAsync(Matches[0]) : Task.CompletedTask;
        }

        if (_catalog.FindByName(name) is { } existing)
        {
            return _excluded().Contains(existing.Id) ? Task.CompletedTask : PickAsync(existing);
        }

        return Matches.Count == 1 ? PickAsync(Matches[0]) : CreateAsync();
    }

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        var excluded = _excluded();
        var query = Domain.Entities.Tag.FoldName(Query.Trim());
        Matches = [.. _catalog.Tags.Where(t => !excluded.Contains(t.Id) && Domain.Entities.Tag.FoldName(t.Name).Contains(query, StringComparison.Ordinal))];
        CanCreate = Query.Trim().Length > 0 && _catalog.FindByName(Query) is null;
        OnPropertyChanged(nameof(IsEmpty));
    }
}
