using AIDatasetStudio.Application.Settings;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Settings;

/// <summary>
/// Stores user settings in <c>app.settings.db</c>. Values are cached in memory; every change is
/// written through immediately. If the file cannot be used (read-only folder, corrupt file), the
/// settings keep working in memory for the session instead of failing the app.
/// </summary>
internal sealed class SqliteUserSettings : IUserSettings
{
    public const string FileName = "app.settings.db";
    public const int MaxRecentWorkspaces = 10;

    private const string UiLanguageKey = "ui.language";
    private const string UiThemeKey = "ui.theme";
    private const string CheckForUpdatesKey = "updates.check";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private string? _connectionString;
    private string? _uiLanguage;
    private string? _uiTheme;
    private bool _checkForUpdates;
    private readonly List<string> _recent = [];

    public SqliteUserSettings(string directory, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        try
        {
            Directory.CreateDirectory(directory);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, FileName),
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Settings are touched rarely; don't keep the file open between uses.
                Pooling = false,
            }.ToString();
            Load();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _connectionString = null;
        }
    }

    /// <summary>
    /// <c>false</c> when the settings file could not be used and changes are kept in memory only.
    /// </summary>
    public bool IsPersistent => _connectionString is not null;

    public string? UiLanguage
    {
        get
        {
            lock (_lock)
            {
                return _uiLanguage;
            }
        }
    }

    public string? UiTheme
    {
        get
        {
            lock (_lock)
            {
                return _uiTheme;
            }
        }
    }

    public IReadOnlyList<string> RecentWorkspaces
    {
        get
        {
            lock (_lock)
            {
                return [.. _recent];
            }
        }
    }

    public bool CheckForUpdates
    {
        get
        {
            lock (_lock)
            {
                return _checkForUpdates;
            }
        }
    }

    public void SetCheckForUpdates(bool enabled)
    {
        lock (_lock)
        {
            _checkForUpdates = enabled;
            SaveValue(CheckForUpdatesKey, enabled ? "true" : null);
        }
    }

    public void SetUiLanguage(string? cultureName)
    {
        lock (_lock)
        {
            _uiLanguage = Normalize(cultureName);
            SaveValue(UiLanguageKey, _uiLanguage);
        }
    }

    public void SetUiTheme(string? theme)
    {
        lock (_lock)
        {
            _uiTheme = Normalize(theme);
            SaveValue(UiThemeKey, _uiTheme);
        }
    }

    public void AddRecentWorkspace(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        lock (_lock)
        {
            _recent.RemoveAll(path => PathComparer.Equals(path, fullPath));
            _recent.Insert(0, fullPath);
            if (_recent.Count > MaxRecentWorkspaces)
            {
                _recent.RemoveRange(MaxRecentWorkspaces, _recent.Count - MaxRecentWorkspaces);
            }

            SaveRecent();
        }
    }

    public void RemoveRecentWorkspace(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        lock (_lock)
        {
            if (_recent.RemoveAll(path => PathComparer.Equals(path, fullPath)) > 0)
            {
                SaveRecent();
            }
        }
    }

    private void Load()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS RecentWorkspaces (Path TEXT PRIMARY KEY, LastOpenedAt INTEGER NOT NULL);
                """;
            create.ExecuteNonQuery();
        }

        _uiLanguage = ReadValue(connection, UiLanguageKey);
        _uiTheme = ReadValue(connection, UiThemeKey);
        _checkForUpdates = ReadValue(connection, CheckForUpdatesKey) == "true";

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Path FROM RecentWorkspaces ORDER BY LastOpenedAt DESC, rowid DESC LIMIT $max;";
            read.Parameters.AddWithValue("$max", MaxRecentWorkspaces);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                _recent.Add(reader.GetString(0));
            }
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadValue(SqliteConnection connection, string key)
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        read.Parameters.AddWithValue("$key", key);
        return read.ExecuteScalar() as string;
    }

    private void SaveValue(string key, string? value) => Write(command =>
    {
        command.Parameters.AddWithValue("$key", key);
        if (value is null)
        {
            command.CommandText = "DELETE FROM Settings WHERE Key = $key;";
        }
        else
        {
            command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;";
            command.Parameters.AddWithValue("$value", value);
        }

        command.ExecuteNonQuery();
    });

    // Rewrites the whole (at most ten row) list so the stored order always matches memory.
    private void SaveRecent()
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Write(command =>
        {
            command.CommandText = "DELETE FROM RecentWorkspaces;";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO RecentWorkspaces (Path, LastOpenedAt) VALUES ($path, $at);";
            var path = command.Parameters.Add(new SqliteParameter { ParameterName = "$path" });
            var at = command.Parameters.Add(new SqliteParameter { ParameterName = "$at" });
            for (var i = 0; i < _recent.Count; i++)
            {
                path.Value = _recent[i];
                at.Value = now - i; // keeps most-recent-first order on reload
                command.ExecuteNonQuery();
            }
        });
    }

    private void Write(Action<SqliteCommand> write)
    {
        if (_connectionString is null)
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            write(command);
            transaction.Commit();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Keep the in-memory value; losing a preference must never crash the app.
            _connectionString = null;
        }
    }
}
