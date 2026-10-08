namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// A newer version that can be installed.
/// </summary>
/// <param name="Version">The new version, e.g. "1.1.0".</param>
/// <param name="Release">What the update library needs to download and apply it.</param>
public sealed record AvailableUpdate(string Version, object Release);

/// <summary>
/// Finds and installs new versions. Only called when the user asks or has turned on the check
/// at startup; the app makes no network request otherwise.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// The running copy was installed with an installer and can update itself (not a build run
    /// from source).
    /// </summary>
    bool IsSupported { get; }

    /// <returns>The newer version, or <c>null</c> when this is the latest.</returns>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    /// <param name="progress">Percent downloaded, 0–100.</param>
    Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exits, installs the downloaded version and starts it.
    /// </summary>
    void RestartToApply(AvailableUpdate update);
}
