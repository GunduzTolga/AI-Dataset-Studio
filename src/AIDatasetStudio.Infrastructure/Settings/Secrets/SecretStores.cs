using AIDatasetStudio.Application.Settings;

namespace AIDatasetStudio.Infrastructure.Settings.Secrets;

/// <summary>
/// The operating system's key store for this computer (ADR-041).
/// </summary>
internal static class SecretStores
{
    public static ISecretStore ForThisSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainStore();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            return new LibSecretStore();
        }

        return NoSecretStore.Instance;
    }
}

/// <summary>
/// No key store: nothing is kept and nothing is found.
/// </summary>
internal sealed class NoSecretStore : ISecretStore
{
    public static readonly NoSecretStore Instance = new();

    private NoSecretStore()
    {
    }

    public SecretStoreKind Kind => SecretStoreKind.None;

    public bool IsAvailable => false;

    public string? Read(string name) => null;

    public bool Write(string name, string secret) => false;

    public void Delete(string name)
    {
    }
}
