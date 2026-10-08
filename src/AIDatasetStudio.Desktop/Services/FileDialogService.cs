using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Localization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AIDatasetStudio.Desktop.Services;

internal sealed class FileDialogService(Func<TopLevel?> topLevel, Localizer localizer) : IFileDialogService
{
    public async Task<string?> PickWorkspaceToOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = localizer["Dialog_OpenWorkspaceTitle"],
            AllowMultiple = false,
            FileTypeFilter = [WorkspaceFileType(), FilePickerFileTypes.All],
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickWorkspaceToCreateAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = localizer["Dialog_NewWorkspaceTitle"],
            SuggestedFileName = suggestedName + WorkspaceFile.Extension,
            DefaultExtension = WorkspaceFile.Extension.TrimStart('.'),
            FileTypeChoices = [WorkspaceFileType()],
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    private FilePickerFileType WorkspaceFileType() => new(localizer["Dialog_WorkspaceFileType"])
    {
        Patterns = ["*" + WorkspaceFile.Extension],
    };

    private IStorageProvider StorageProvider =>
        topLevel()?.StorageProvider ?? throw new InvalidOperationException("No window is available to host a file dialog.");
}
