using AIDatasetStudio.Application.Settings;

namespace AIDatasetStudio.Infrastructure.Tests.Support;

/// <summary>
/// A key store kept in memory, counting its writes; can be switched off like a locked keyring.
/// </summary>
internal sealed class MemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Items { get; } = [];

    public int Writes { get; private set; }

    public bool IsAvailable { get; set; } = true;

    public SecretStoreKind Kind => SecretStoreKind.SecretService;

    public string? Read(string name) => IsAvailable ? Items.GetValueOrDefault(name) : null;

    public bool Write(string name, string secret)
    {
        if (!IsAvailable)
        {
            return false;
        }

        Writes++;
        Items[name] = secret;
        return true;
    }

    public void Delete(string name) => Items.Remove(name);
}
