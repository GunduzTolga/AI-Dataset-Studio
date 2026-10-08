namespace AIDatasetStudio.Infrastructure.Settings;

/// <summary>
/// Where per-user settings live (SDD §3.1).
/// </summary>
public static class UserSettingsLocation
{
    /// <summary>
    /// Overrides the settings folder, e.g. for a portable install or tests.
    /// </summary>
    public const string OverrideVariable = "AIDATASETSTUDIO_SETTINGS_DIR";

    private const string AppFolder = "AIDatasetStudio";

    /// <summary>
    /// Windows: %APPDATA%\AIDatasetStudio; macOS: ~/Library/Application Support/AIDatasetStudio;
    /// Linux: $XDG_CONFIG_HOME/AIDatasetStudio (default ~/.config/AIDatasetStudio).
    /// </summary>
    public static string GetDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppFolder);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);
    }
}
