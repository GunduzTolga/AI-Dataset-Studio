namespace AIDatasetStudio.Application.Settings;

/// <summary>
/// Where the operating system keeps secrets for the user (SDD §17.1, §19.13).
/// </summary>
public enum SecretStoreKind
{
    /// <summary>No key store could be used on this computer.</summary>
    None,

    /// <summary>Windows Credential Manager.</summary>
    WindowsCredentialManager,

    /// <summary>The macOS login keychain.</summary>
    MacKeychain,

    /// <summary>The freedesktop Secret Service: GNOME Keyring, KWallet or KeePassXC.</summary>
    SecretService,
}

/// <summary>
/// The operating system's key store, for API keys of cloud model services (M19). Secrets are
/// protected by the user's login and never written to settings or workspace files.
/// </summary>
/// <remarks>
/// Calls are synchronous and quick; a key store that cannot be reached reports itself as
/// unavailable instead of throwing, and callers fall back as ADR-041 describes.
/// </remarks>
public interface ISecretStore
{
    SecretStoreKind Kind { get; }

    /// <summary>
    /// Whether secrets can be kept: <c>false</c> on a computer without a key store, e.g. a Linux
    /// desktop without a Secret Service. Checked once.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The secret stored under <paramref name="name"/>, or <c>null</c> when there is none or the
    /// key store cannot be read.
    /// </summary>
    string? Read(string name);

    /// <summary>
    /// Stores or replaces a secret; <c>false</c> when the key store refused or cannot be used.
    /// </summary>
    bool Write(string name, string secret);

    /// <summary>
    /// Removes a secret if there is one.
    /// </summary>
    void Delete(string name);
}
