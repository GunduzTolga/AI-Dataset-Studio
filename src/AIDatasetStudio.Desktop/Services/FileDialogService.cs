using AIDatasetStudio.Application.Workspaces;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AIDatasetStudio.Desktop.Services;

internal sealed class FileDialogService(Func<TopLevel?> topLevel) : IFileDialogService
{
    private static readonly FilePickerFileType WorkspaceFileType = new(WorkspaceFile.DisplayName)
    {
        Patterns = ["*" + WorkspaceFile.Extension],
    };

    public async Task<string?> PickWorkspaceToOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Workspace",
            AllowMultiple = false,
            FileTypeFilter = [WorkspaceFileType, FilePickerFileTypes.All],
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickWorkspaceToCreateAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "New Workspace",
            SuggestedFileName = suggestedName + WorkspaceFile.Extension,
            DefaultExtension = WorkspaceFile.Extension.TrimStart('.'),
            FileTypeChoices = [WorkspaceFileType],
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    private IStorageProvider StorageProvider =>
        topLevel()?.StorageProvider ?? throw new InvalidOperationException("No window is available to host a file dialog.");
}
