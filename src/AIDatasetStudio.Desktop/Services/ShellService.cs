using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AIDatasetStudio.Desktop.Services;

internal sealed class ShellService(Func<Window?> owner) : IShellService
{
    public async Task OpenFolderAsync(string path)
    {
        Directory.CreateDirectory(path);
        if (owner()?.Launcher is { } launcher)
        {
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
    }

    public async Task OpenFileAsync(string path)
    {
        if (owner()?.Launcher is { } launcher && File.Exists(path))
        {
            await launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
    }
}
