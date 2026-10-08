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

    /// <param name="extensions">Extensions to offer, with the leading dot.</param>
    /// <returns>The chosen local path, or <c>null</c> if the user cancelled.</returns>
    Task<string?> PickImportFileAsync(IReadOnlyList<string> extensions);

    /// <returns>The JSON Lines file to write, or <c>null</c> if the user cancelled. Overwriting was already confirmed.</returns>
    Task<string?> PickExportFileAsync(string suggestedName);

    /// <returns>The folder for split files, or <c>null</c> if the user cancelled.</returns>
    Task<string?> PickExportFolderAsync();
}
