using System.Text.Json;
using System.Text.Json.Serialization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Infrastructure.Settings.Secrets;
using Microsoft.Data.Sqlite;

namespace AIDatasetStudio.Infrastructure.Settings;

/// <summary>
/// Stores user settings, AI connections included, in <c>app.settings.db</c>. Values are cached in
/// memory; every change is written through immediately. If the file cannot be used (read-only
/// folder, corrupt file), the settings keep working in memory for the session instead of failing
/// the app. API keys go to the operating system's key store; only without one are they kept in
/// the file, which only the user can read (ADR-041).
/// </summary>
internal sealed class SqliteUserSettings : IUserSettings, IAIConnectionStore
{
    public const string FileName = "app.settings.db";
    public const int MaxRecentWorkspaces = 10;

    private const string UiLanguageKey = "ui.language";
    private const string UiThemeKey = "ui.theme";
    private const string CheckForUpdatesKey = "updates.check";
    private const string AIConnectionsKey = "ai.connections";
    private const string AIDefaultKey = "ai.default";

    // Nulls are written: a missing value would come back as the constructor's default.
    private static readonly JsonSerializerOptions ConnectionJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly ISecretStore _secrets;

    // The key last written to the key store per connection, so saving other fields does not write it again.
    private readonly Dictionary<Guid, string> _storedKeys = [];
    private string? _connectionString;
    private string? _uiLanguage;
    private string? _uiTheme;
    private bool _checkForUpdates;
    private readonly List<string> _recent = [];
    private readonly List<AIConnection> _connections = [];
    private Guid? _defaultConnection;

    /// <param name="secrets">Where API keys are kept; without one, they are kept in the settings file.</param>
    public SqliteUserSettings(string directory, TimeProvider timeProvider, ISecretStore? secrets = null)
    {
        _timeProvider = timeProvider;
        _secrets = secrets ?? NoSecretStore.Instance;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Settings are touched rarely; don't keep the file open between uses.
                Pooling = false,
            }.ToString();
            Load();
            KeepPrivate(path);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _connectionString = null;
        }

        MoveKeysToKeyStore();
    }

    public SecretStoreKind KeyStore => _secrets.IsAvailable ? _secrets.Kind : SecretStoreKind.None;

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

    public event EventHandler? Changed;

    public IReadOnlyList<AIConnection> Connections
    {
        get
        {
            lock (_lock)
            {
                return [.. _connections];
            }
        }
    }

    public AIConnection? DefaultConnection
    {
        get
        {
            lock (_lock)
            {
                return _connections.FirstOrDefault(c => c.Id == _defaultConnection) ?? _connections.FirstOrDefault();
            }
        }
    }

    public AIConnection Save(AIConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        bool hadStoredKey;
        lock (_lock)
        {
            hadStoredKey = _connections.Any(c => c.Id == connection.Id && c.HasStoredKey);
        }

        // The key store is asked outside the lock: it may take a moment.
        var saved = KeepKey(connection, hadStoredKey);
        lock (_lock)
        {
            var index = _connections.FindIndex(c => c.Id == saved.Id);
            if (index >= 0)
            {
                _connections[index] = saved;
            }
            else
            {
                _connections.Add(saved);
            }

            _defaultConnection ??= saved.Id;
            SaveConnections();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public void Remove(Guid connectionId)
    {
        bool hadStoredKey;
        lock (_lock)
        {
            hadStoredKey = _connections.Any(c => c.Id == connectionId && c.HasStoredKey);
            if (_connections.RemoveAll(c => c.Id == connectionId) == 0)
            {
                return;
            }

            if (_defaultConnection == connectionId)
            {
                _defaultConnection = _connections.FirstOrDefault()?.Id;
            }

            SaveConnections();
            _storedKeys.Remove(connectionId);
        }

        if (hadStoredKey)
        {
            _secrets.Delete(AIProviders.SecretName(connectionId));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The connection as it is kept: a typed key moves to the key store when there is one, and
    /// a stored key the connection no longer has is deleted from it.
    /// </summary>
    private AIConnection KeepKey(AIConnection connection, bool hadStoredKey)
    {
        var name = AIProviders.SecretName(connection.Id);
        if (connection.ApiKey?.Trim() is { Length: > 0 } key)
        {
            lock (_lock)
            {
                if (_storedKeys.TryGetValue(connection.Id, out var stored) && stored == key)
                {
                    return connection with { ApiKey = null, HasStoredKey = true };
                }
            }

            if (_secrets.IsAvailable && _secrets.Write(name, key))
            {
                lock (_lock)
                {
                    _storedKeys[connection.Id] = key;
                }

                return connection with { ApiKey = null, HasStoredKey = true };
            }

            // No key store: the key stays in the settings file (ADR-041).
            if (hadStoredKey)
            {
                _secrets.Delete(name);
            }

            return connection with { ApiKey = key, HasStoredKey = false };
        }

        if (hadStoredKey && !connection.HasStoredKey)
        {
            _secrets.Delete(name);
            lock (_lock)
            {
                _storedKeys.Remove(connection.Id);
            }
        }

        return connection with { ApiKey = null, HasStoredKey = connection.HasStoredKey && hadStoredKey };
    }

    /// <summary>
    /// Keys kept in the settings file, by earlier versions or while there was no key store, move
    /// to the key store once there is one. Nothing is asked of the key store when there are none.
    /// </summary>
    private void MoveKeysToKeyStore()
    {
        List<AIConnection> plain;
        lock (_lock)
        {
            plain = [.. _connections.Where(c => !string.IsNullOrWhiteSpace(c.ApiKey))];
        }

        if (plain.Count == 0 || !_secrets.IsAvailable)
        {
            return;
        }

        foreach (var connection in plain)
        {
            var moved = KeepKey(connection, hadStoredKey: false);
            lock (_lock)
            {
                var index = _connections.FindIndex(c => c.Id == moved.Id);
                if (index >= 0)
                {
                    _connections[index] = moved;
                }
            }
        }

        lock (_lock)
        {
            SaveConnections();
        }
    }

    /// <summary>
    /// On Linux and macOS, only the user may read the settings file: it can hold API keys when
    /// there is no key store.
    /// </summary>
    private static void KeepPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A file system without Unix permissions; nothing to tighten.
        }
    }

    public void SetDefault(Guid connectionId)
    {
        lock (_lock)
        {
            if (_defaultConnection == connectionId || _connections.All(c => c.Id != connectionId))
            {
                return;
            }

            _defaultConnection = connectionId;
            SaveConnections();
        }

        Changed?.Invoke(this, EventArgs.Empty);
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
        LoadConnections(ReadValue(connection, AIConnectionsKey), ReadValue(connection, AIDefaultKey));

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

    private void LoadConnections(string? json, string? defaultId)
    {
        try
        {
            _connections.AddRange(JsonSerializer.Deserialize<List<AIConnection>>(json ?? "[]", ConnectionJson) ?? []);
        }
        catch (JsonException)
        {
            // A damaged list is dropped rather than keeping the app from starting.
        }

        _defaultConnection = Guid.TryParse(defaultId, out var id) ? id : null;
    }

    // The whole (short) list is one JSON value. Keys are in it only without a key store; they stay
    // on this machine, never in workspaces.
    private void SaveConnections()
    {
        SaveValue(AIConnectionsKey, _connections.Count == 0 ? null : JsonSerializer.Serialize(_connections, ConnectionJson));
        SaveValue(AIDefaultKey, _defaultConnection?.ToString());
    }

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
            using (var pragma = connection.CreateCommand())
            {
                // A key moved to the key store leaves no copy in the file's free pages.
                pragma.CommandText = "PRAGMA secure_delete = ON;";
                pragma.ExecuteNonQuery();
            }

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
