namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Opens folders and files with the operating system's default applications.
/// </summary>
public interface IShellService
{
    /// <summary>
    /// Shows a folder in the file manager, creating it first if needed.
    /// </summary>
    Task OpenFolderAsync(string path);

    Task OpenFileAsync(string path);
}
