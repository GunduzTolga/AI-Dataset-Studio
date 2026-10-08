using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

public sealed class RecentWorkspaceViewModel(string filePath, IAsyncRelayCommand<string> openCommand)
{
    public string FilePath { get; } = filePath;

    /// <summary>
    /// File name without the folder; the full path is the tooltip.
    /// </summary>
    public string DisplayName { get; } = Path.GetFileName(filePath);

    public IAsyncRelayCommand<string> OpenCommand { get; } = openCommand;
}
