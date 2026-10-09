using AIDatasetStudio.Application.Settings;

namespace AIDatasetStudio.Desktop.Tests.Support;

/// <summary>
/// A key store in memory, standing in for the operating system's.
/// </summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Items { get; } = [];

    public bool IsAvailable { get; set; } = true;

    public SecretStoreKind Kind => SecretStoreKind.WindowsCredentialManager;

    public string? Read(string name) => IsAvailable ? Items.GetValueOrDefault(name) : null;

    public bool Write(string name, string secret)
    {
        if (!IsAvailable)
        {
            return false;
        }

        Items[name] = secret;
        return true;
    }

    public void Delete(string name) => Items.Remove(name);
}
