using AIDatasetStudio.Application.Export;
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

    public async Task<string?> PickImportFileAsync(IReadOnlyList<string> extensions)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = localizer["Dialog_ImportFileTitle"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(localizer["Dialog_ImportFileType"]) { Patterns = extensions.Select(e => "*" + e).ToList() },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickDocumentAsync(IReadOnlyList<string> extensions)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = localizer["Dialog_DocumentTitle"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(localizer["Dialog_DocumentType"]) { Patterns = extensions.Select(e => "*" + e).ToList() },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickExportFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = localizer["Dialog_ExportFileTitle"],
            SuggestedFileName = suggestedName,
            DefaultExtension = ExportFormats.Extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(localizer["Dialog_ExportFileType"]) { Patterns = ["*" + ExportFormats.Extension] }],
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickExportFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = localizer["Dialog_ExportFolderTitle"],
            AllowMultiple = false,
        });

        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickTrainingToolAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = localizer["Dialog_TrainingToolTitle"],
            AllowMultiple = false,
        });

        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickTrainingFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = localizer["Dialog_TrainingFolderTitle"],
            AllowMultiple = false,
        });

        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }

    private FilePickerFileType WorkspaceFileType() => new(localizer["Dialog_WorkspaceFileType"])
    {
        Patterns = ["*" + WorkspaceFile.Extension],
    };

    private IStorageProvider StorageProvider =>
        topLevel()?.StorageProvider ?? throw new InvalidOperationException("No window is available to host a file dialog.");
}
