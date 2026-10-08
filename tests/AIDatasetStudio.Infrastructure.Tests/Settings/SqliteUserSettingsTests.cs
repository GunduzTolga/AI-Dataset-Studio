using AIDatasetStudio.Infrastructure.Settings;

namespace AIDatasetStudio.Infrastructure.Tests.Settings;

public sealed class SqliteUserSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Fresh_settings_follow_the_os_and_have_no_recent_files()
    {
        var settings = new SqliteUserSettings(_directory, _clock);

        Assert.True(settings.IsPersistent);
        Assert.Null(settings.UiLanguage);
        Assert.Empty(settings.RecentWorkspaces);
        Assert.False(settings.CheckForUpdates); // no network request unless the user asks
        Assert.True(File.Exists(Path.Combine(_directory, SqliteUserSettings.FileName)));
    }

    [Fact]
    public void Language_survives_a_restart_and_can_be_cleared()
    {
        new SqliteUserSettings(_directory, _clock).SetUiLanguage("tr");

        var reloaded = new SqliteUserSettings(_directory, _clock);
        Assert.Equal("tr", reloaded.UiLanguage);

        reloaded.SetUiLanguage(null);
        Assert.Null(new SqliteUserSettings(_directory, _clock).UiLanguage);
    }

    [Fact]
    public void Update_check_choice_survives_a_restart()
    {
        new SqliteUserSettings(_directory, _clock).SetCheckForUpdates(true);
        var reloaded = new SqliteUserSettings(_directory, _clock);
        Assert.True(reloaded.CheckForUpdates);

        reloaded.SetCheckForUpdates(false);
        Assert.False(new SqliteUserSettings(_directory, _clock).CheckForUpdates);
    }

    [Fact]
    public void Theme_survives_a_restart_independently_of_language()
    {
        var settings = new SqliteUserSettings(_directory, _clock);
        settings.SetUiTheme("dark");
        settings.SetUiLanguage("tr");
        settings.SetUiTheme(null);
        settings.SetUiTheme("light");

        var reloaded = new SqliteUserSettings(_directory, _clock);
        Assert.Equal("light", reloaded.UiTheme);
        Assert.Equal("tr", reloaded.UiLanguage);
    }

    [Fact]
    public void Recent_workspaces_are_most_recent_first_without_duplicates()
    {
        var settings = new SqliteUserSettings(_directory, _clock);
        var a = Path.Combine(_directory, "a.adstudio");
        var b = Path.Combine(_directory, "b.adstudio");

        settings.AddRecentWorkspace(a);
        settings.AddRecentWorkspace(b);
        settings.AddRecentWorkspace(a);

        Assert.Equal([a, b], settings.RecentWorkspaces);
        Assert.Equal([a, b], new SqliteUserSettings(_directory, _clock).RecentWorkspaces);
    }

    [Fact]
    public void Recent_list_is_capped()
    {
        var settings = new SqliteUserSettings(_directory, _clock);
        var paths = Enumerable.Range(0, 15).Select(i => Path.Combine(_directory, $"{i}.adstudio")).ToList();

        foreach (var path in paths)
        {
            settings.AddRecentWorkspace(path);
        }

        var expected = Enumerable.Reverse(paths).Take(SqliteUserSettings.MaxRecentWorkspaces).ToList();
        Assert.Equal(expected, settings.RecentWorkspaces);
        Assert.Equal(expected, new SqliteUserSettings(_directory, _clock).RecentWorkspaces);
    }

    [Fact]
    public void Removed_workspaces_stay_removed()
    {
        var settings = new SqliteUserSettings(_directory, _clock);
        var gone = Path.Combine(_directory, "gone.adstudio");
        settings.AddRecentWorkspace(gone);

        settings.RemoveRecentWorkspace(gone);

        Assert.Empty(new SqliteUserSettings(_directory, _clock).RecentWorkspaces);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_memory()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, SqliteUserSettings.FileName), new string('x', 4096));

        var settings = new SqliteUserSettings(_directory, _clock);
        settings.SetUiLanguage("en");

        Assert.False(settings.IsPersistent);
        Assert.Equal("en", settings.UiLanguage);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
