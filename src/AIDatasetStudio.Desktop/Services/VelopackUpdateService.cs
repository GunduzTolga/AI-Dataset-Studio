using Velopack;
using Velopack.Sources;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Updates from the repository's GitHub Releases, published by the release workflow with Velopack
/// (one channel per platform, so each install only sees its own packages).
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    private readonly Lazy<UpdateManager?> _manager = new(Create);

    public bool IsSupported => _manager.Value?.IsInstalled == true;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var manager = Manager();
        cancellationToken.ThrowIfCancellationRequested();
        return await manager.CheckForUpdatesAsync() is { } info
            ? new AvailableUpdate(info.TargetFullRelease.Version.ToString(), info)
            : null;
    }

    public Task DownloadAsync(AvailableUpdate update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return Manager().DownloadUpdatesAsync((UpdateInfo)update.Release, percent => progress?.Report(percent), cancellationToken);
    }

    public void RestartToApply(AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Manager().ApplyUpdatesAndRestart(((UpdateInfo)update.Release).TargetFullRelease);
    }

    private UpdateManager Manager() =>
        _manager.Value is { IsInstalled: true } manager
            ? manager
            : throw new InvalidOperationException("Updates are only available in installed builds.");

    private static UpdateManager? Create()
    {
        try
        {
            return new UpdateManager(new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            return null; // not installed by Velopack (e.g. run from source)
        }
    }
}
