using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Updates, the about box and the error log (M6).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class HelpMenuTests
{
    public HelpMenuTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static string Version => AppInfo.Version;

    [Fact]
    public void The_version_comes_from_the_build_without_its_commit()
    {
        Assert.Equal(typeof(AppInfo).Assembly.GetName().Version!.ToString(3), Version);
    }

    [Fact]
    public Task Without_a_newer_version_the_check_says_so() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();

        await host.Main.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, host.Updates.Checks);
        Assert.Equal($"AI Dataset Studio {Version} is the latest version.", host.Main.Status.Message);
    });

    [Fact]
    public Task A_newer_version_is_installed_after_saving_and_closing_the_workspace() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(2);
        var update = new AvailableUpdate("1.1.0", new object());
        host.Updates.Next = update;

        await host.Main.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal($"Version 1.1.0 is available (you have {Version}). Download it and restart? Your work is saved first.", host.Dialogs.ConfirmMessages[^1]);
        Assert.Same(update, host.Updates.Downloaded);
        Assert.Same(update, host.Updates.Applied);
        Assert.False(host.Main.HasWorkspace); // the WAL is checkpointed before the app exits
    });

    [Fact]
    public Task Declining_keeps_the_update_for_later() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.Updates.Next = new AvailableUpdate("1.1.0", new object());
        host.Dialogs.ConfirmAnswer = false;

        await host.Main.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Null(host.Updates.Downloaded);
        Assert.Equal("Version 1.1.0 is available. Help › Check for Updates installs it.", host.Main.Status.Message);
        Assert.Equal("1.1.0", host.Main.AvailableUpdate?.Version);
    });

    [Fact]
    public Task Builds_that_were_not_installed_never_go_online() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.Updates.IsSupported = false;
        host.Main.CheckForUpdatesAtStartup = true;

        await host.Main.CheckForUpdatesCommand.ExecuteAsync(null);
        await host.Main.CheckForUpdatesAtStartupAsync();

        Assert.Equal(0, host.Updates.Checks);
        Assert.Equal($"Updates are installed by the installed app; this copy ({Version}) was started from a build.", host.Main.Status.Message);
    });

    [Fact]
    public Task The_startup_check_runs_only_when_turned_on() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.Updates.Next = new AvailableUpdate("1.1.0", new object());

        Assert.False(host.Main.CheckForUpdatesAtStartup);
        await host.Main.CheckForUpdatesAtStartupAsync();
        Assert.Equal(0, host.Updates.Checks);

        host.Main.CheckForUpdatesAtStartup = true;
        Assert.True(host.Settings.CheckForUpdates);
        await host.Main.CheckForUpdatesAtStartupAsync();
        Assert.Equal(1, host.Updates.Checks);
        Assert.Equal("Version 1.1.0 is available. Help › Check for Updates installs it.", host.Main.Status.Message);

        // Offline at startup: nothing to report.
        host.Updates.Failure = new HttpRequestException("offline");
        host.Main.Status.Set("Status_Ready");
        await host.Main.CheckForUpdatesAtStartupAsync();
        Assert.Equal("Ready", host.Main.Status.Message);
    });

    [Fact]
    public Task A_failed_check_is_reported_when_asked_for() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.Updates.Failure = new HttpRequestException("No such host is known.");

        await host.Main.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("Could not check for updates: No such host is known.", host.Main.Status.Message);
        Assert.True(host.Main.CheckForUpdatesCommand.CanExecute(null));
    });

    [Fact]
    public Task About_shows_the_version_and_opens_the_log_folder() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();

        await host.Main.AboutCommand.ExecuteAsync(null);

        var about = host.Dialogs.LastAbout!;
        Assert.Equal($"Version {Version}", about.VersionText);
        Assert.Equal(Path.Combine(about.DataFolder, "logs"), about.LogFolder);
        await about.OpenLogFolderCommand.ExecuteAsync(null);
        await about.OpenNoticesCommand.ExecuteAsync(null);
        Assert.Equal([about.LogFolder, AppInfo.NoticesPath], host.Shell.Opened);
        Assert.True(File.Exists(AppInfo.NoticesPath)); // shipped next to the app
    });

    [Fact]
    public void Errors_are_appended_to_a_daily_log()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = CrashLog.Write(new InvalidOperationException("first failure"), directory);
            var second = CrashLog.Write(new IOException("second failure"), directory);

            Assert.Equal(first, second);
            var text = File.ReadAllText(first!);
            Assert.Contains($"AI Dataset Studio {Version}", text, StringComparison.Ordinal);
            Assert.Contains("System.InvalidOperationException: first failure", text, StringComparison.Ordinal);
            Assert.Contains("System.IO.IOException: second failure", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
