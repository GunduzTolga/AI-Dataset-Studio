using System.Reflection;
using AIDatasetStudio.Infrastructure.Settings;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// The application's version and the places it keeps its own files.
/// </summary>
internal static class AppInfo
{
    /// <summary>
    /// Where releases are published; the update check reads its GitHub Releases.
    /// </summary>
    public const string RepositoryUrl = "https://github.com/GunduzTolga/AI-Dataset-Studio";

    /// <summary>
    /// "1.0.0": the informational version without the build metadata (commit hash).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// Per-user settings (SDD §3.1) and, in a subfolder, the error logs.
    /// </summary>
    public static string DataDirectory => UserSettingsLocation.GetDirectory();

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");

    private static string ReadVersion()
    {
        var version = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }
}
