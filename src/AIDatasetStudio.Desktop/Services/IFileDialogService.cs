namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// File pickers, abstracted so view models do not depend on a window.
/// </summary>
public interface IFileDialogService
{
    /// <returns>The chosen local path, or <c>null</c> if the user cancelled.</returns>
    Task<string?> PickWorkspaceToOpenAsync();

    /// <returns>The chosen local path, or <c>null</c> if the user cancelled. Overwriting was already confirmed.</returns>
    Task<string?> PickWorkspaceToCreateAsync(string suggestedName);
}
