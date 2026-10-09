using AIDatasetStudio.Desktop.ViewModels.Tags;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// Tags (SDD §6.2): Mod+1…5, Record › Tag and Tools › Tags.
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The workspace's tags (Record › Tag lists them with their shortcuts).
    /// </summary>
    public TagCatalog Tags => _tags;

    /// <summary>
    /// Adds the tag to the shown record (or all selected records), or removes it when they already have it.
    /// </summary>
    /// <param name="parameter">The tag. Typed loosely: a menu item briefly passes its parent's
    /// data while its own is being set.</param>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task ToggleTagAsync(object? parameter) => parameter is TagViewModel tag ? Details.ToggleTagAsync(tag) : Task.CompletedTask;

    /// <summary>
    /// Mod+1 … Mod+5: toggles the tag with that shortcut.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task ToggleTagSlotAsync(int slot)
    {
        if (_tags.ForShortcut(slot) is { } tag)
        {
            return Details.ToggleTagAsync(tag);
        }

        Status.Set("Status_NoTagShortcut", TagCatalog.ShortcutText(slot));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tools › Tags: names, colors, shortcuts; deleting a tag takes it off its records.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCloseWorkspace))]
    private async Task ManageTagsAsync()
    {
        var manager = new TagManagerViewModel(_tagService, _tags, _dialogs, _localizer);
        await _dialogs.ShowTagManagerAsync(manager);
        try
        {
            if (manager.HasDeleted)
            {
                // Records lost tags: lists filtered by tag and the inspector show them again.
                await Records.ReloadAsync();
                await Details.ReloadAsync();
            }

            if (manager.ShowTagRequested is { } tagId)
            {
                Records.ShowTag(tagId);
            }
        }
        catch (Exception ex) when (Status.TryReport(ex))
        {
        }
    }
}
