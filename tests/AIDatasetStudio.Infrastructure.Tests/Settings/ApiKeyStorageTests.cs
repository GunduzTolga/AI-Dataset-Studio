using System.Text;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Settings;
using AIDatasetStudio.Infrastructure.AI;
using AIDatasetStudio.Infrastructure.Settings;
using AIDatasetStudio.Infrastructure.Settings.Secrets;
using AIDatasetStudio.Infrastructure.Tests.Support;

namespace AIDatasetStudio.Infrastructure.Tests.Settings;

/// <summary>
/// API keys of AI connections in the operating system's key store, and the explicit fallback
/// without one (M19, ADR-041).
/// </summary>
public sealed class ApiKeyStorageTests : IDisposable
{
    private const string Key = "sk-test-0123456789abcdef";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adstudio-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 9, 2, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SettingsFile => Path.Combine(_directory, SqliteUserSettings.FileName);

    [Fact]
    public void Keys_go_to_the_key_store_and_never_into_the_settings_file()
    {
        var secrets = new MemorySecretStore();
        var settings = new SqliteUserSettings(_directory, _clock, secrets);
        var openAI = Connection(AIProviderKind.OpenAI) with { ApiKey = $" {Key} " };

        var saved = settings.Save(openAI);

        Assert.Equal(SecretStoreKind.SecretService, settings.KeyStore);
        Assert.Equal((null, true), (saved.ApiKey, saved.HasStoredKey));
        Assert.Equal(Key, secrets.Items[AIProviders.SecretName(openAI.Id)]);
        Assert.DoesNotContain(Key, FileText(), StringComparison.Ordinal);

        // Other changes keep the key without writing it again.
        settings.Save(openAI with { Name = "OpenAI (iş)" });
        settings.Save(saved with { Model = "gpt-4.1-mini" });
        Assert.Equal(1, secrets.Writes);
        var reloaded = new SqliteUserSettings(_directory, _clock, secrets).DefaultConnection!;
        Assert.Equal(("gpt-4.1-mini", null, true), (reloaded.Model, reloaded.ApiKey, reloaded.HasStoredKey));

        // Taking the key away deletes it; so does deleting the connection.
        Assert.False(settings.Save(reloaded with { HasStoredKey = false }).HasStoredKey);
        Assert.Empty(secrets.Items);
        settings.Save(reloaded with { ApiKey = Key });
        settings.Remove(openAI.Id);
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public void Without_a_key_store_keys_stay_in_a_private_settings_file_until_there_is_one()
    {
        var plain = new SqliteUserSettings(_directory, _clock);
        var groq = Connection(AIProviderKind.Groq) with { ApiKey = Key };

        var saved = plain.Save(groq);

        Assert.Equal(SecretStoreKind.None, plain.KeyStore);
        Assert.Equal((Key, false), (saved.ApiKey, saved.HasStoredKey));
        Assert.Contains(Key, FileText(), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsFile));
        }

        // A locked keyring is no key store either.
        var secrets = new MemorySecretStore { IsAvailable = false };
        Assert.Equal(Key, new SqliteUserSettings(_directory, _clock, secrets).DefaultConnection!.ApiKey);

        // Once there is one, the key moves there and leaves no copy in the file.
        secrets.IsAvailable = true;
        var moved = new SqliteUserSettings(_directory, _clock, secrets).DefaultConnection!;
        Assert.Equal((null, true), (moved.ApiKey, moved.HasStoredKey));
        Assert.Equal(Key, secrets.Items[AIProviders.SecretName(groq.Id)]);
        Assert.DoesNotContain(Key, FileText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Providers_send_the_stored_key_or_the_services_environment_variable()
    {
        await using var server = new FakeOpenAIServer();
        var secrets = new MemorySecretStore();
        using var factory = new AIProviderFactory(secrets);

        // The fake server is on this computer; the kinds only choose how the key is found.
        var stored = Connection(AIProviderKind.OpenAI, server.BaseUrl) with { HasStoredKey = true };
        secrets.Write(AIProviders.SecretName(stored.Id), Key);
        await factory.Create(stored).ListModelsAsync(Ct);
        Assert.Equal($"Bearer {Key}", server.Authorizations.Last());

        Environment.SetEnvironmentVariable("GROQ_API_KEY", "gsk-ortam");
        try
        {
            await factory.Create(Connection(AIProviderKind.Groq, server.BaseUrl)).ListModelsAsync(Ct);
            Assert.Equal("Bearer gsk-ortam", server.Authorizations.Last());

            // The connection's own key comes first.
            await factory.Create(Connection(AIProviderKind.Groq, server.BaseUrl) with { ApiKey = "gsk-kendi" }).ListModelsAsync(Ct);
            Assert.Equal("Bearer gsk-kendi", server.Authorizations.Last());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROQ_API_KEY", null);
        }

        // A cloud service without any key, and a stored key that cannot be read, are explained.
        Assert.Equal(AIErrorKind.NoApiKey, Assert.Throws<AIException>(() => factory.Create(Connection(AIProviderKind.DeepSeek, server.BaseUrl))).Kind);
        secrets.IsAvailable = false;
        Assert.Equal(AIErrorKind.ApiKeyUnreadable, Assert.Throws<AIException>(() => factory.Create(stored)).Kind);

        // Servers people run themselves still need none.
        await factory.Create(Connection(AIProviderKind.Ollama, server.BaseUrl)).ListModelsAsync(Ct);
        Assert.Null(server.Authorizations.Last());
    }

    [Fact]
    public void The_secret_service_keeps_a_key_or_says_it_is_unavailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var store = new LibSecretStore();
        var name = $"test/{Guid.NewGuid():N}";
        if (!store.IsAvailable)
        {
            // No session bus or no Secret Service (CI, minimal desktops): the fallback applies.
            Assert.Null(store.Read(name));
            Assert.False(store.Write(name, Key));
            return;
        }

        try
        {
            Assert.Null(store.Read(name));
            Assert.True(store.Write(name, Key));
            Assert.Equal(Key, store.Read(name));
            Assert.True(store.Write(name, "sk-yenisi-çğüşıö"));
            Assert.Equal("sk-yenisi-çğüşıö", store.Read(name));
        }
        finally
        {
            store.Delete(name);
        }

        Assert.Null(store.Read(name));
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

    private static AIConnection Connection(AIProviderKind kind, string? url = null) =>
        new(Guid.CreateVersion7(), kind.ToString(), kind, url ?? AIProviders.DefaultUrl(kind), "model-a", AllowRemote: true);

    // The whole file, as SQLite keeps it on disk.
    private string FileText() => Encoding.UTF8.GetString(File.ReadAllBytes(SettingsFile));
}
