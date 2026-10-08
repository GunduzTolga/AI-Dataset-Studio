namespace AIDatasetStudio.Application.Settings;

/// <summary>
/// Per-user application preferences kept outside workspace files (SDD §3.1).
/// </summary>
/// <remarks>
/// Settings are tiny and read at startup before any window exists, so the API is synchronous.
/// If the settings file cannot be used, preferences live in memory for the session.
/// </remarks>
public interface IUserSettings
{
    /// <summary>
    /// The chosen UI language (culture name such as "tr"), or <c>null</c> to follow the OS.
    /// </summary>
    string? UiLanguage { get; }

    /// <summary>
    /// The chosen color theme ("light" or "dark"), or <c>null</c> to follow the OS.
    /// </summary>
    string? UiTheme { get; }

    /// <summary>
    /// Recently opened workspace files, most recent first.
    /// </summary>
    IReadOnlyList<string> RecentWorkspaces { get; }

    void SetUiLanguage(string? cultureName);

    void SetUiTheme(string? theme);

    void AddRecentWorkspace(string filePath);

    void RemoveRecentWorkspace(string filePath);
}
