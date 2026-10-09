namespace AIDatasetStudio.Application.AI;

/// <summary>
/// A connection to a model server (SDD §16, §17).
/// </summary>
public interface IAIProvider
{
    AIConnection Connection { get; }

    /// <summary>
    /// The models the server offers, by name.
    /// </summary>
    /// <exception cref="AIException">The server cannot be reached or refuses the request.</exception>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a conversation to the connection's model and yields its answer as it is written.
    /// </summary>
    /// <exception cref="AIException">The server cannot be reached, refuses the request or stops answering.</exception>
    IAsyncEnumerable<string> StreamChatAsync(AIChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns texts into vectors with the connection's embedding model, one per text in order.
    /// </summary>
    /// <exception cref="AIException">No embedding model is chosen, or the server cannot be reached or refuses the request.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}

public interface IAIProviderFactory
{
    /// <exception cref="AIException">The address is invalid, or remote without the user's consent.</exception>
    IAIProvider Create(AIConnection connection);
}

/// <summary>
/// The AI connections of this user, kept with the other user settings.
/// </summary>
public interface IAIConnectionStore
{
    IReadOnlyList<AIConnection> Connections { get; }

    /// <summary>
    /// The connection used for generating, or <c>null</c> when none is set up.
    /// </summary>
    AIConnection? DefaultConnection { get; }

    /// <summary>
    /// Raised after any change, so views showing the active model can follow.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Where API keys are kept: the operating system's key store, or
    /// <see cref="Settings.SecretStoreKind.None"/> when there is none and keys are kept in the
    /// settings file, unencrypted (ADR-041).
    /// </summary>
    Settings.SecretStoreKind KeyStore { get; }

    /// <summary>
    /// Adds the connection or replaces the one with the same id. The first one becomes the default.
    /// An <see cref="AIConnection.ApiKey"/> goes to the key store, and the saved connection has
    /// <see cref="AIConnection.HasStoredKey"/> instead; without either, a stored key is deleted.
    /// </summary>
    /// <returns>The connection as saved.</returns>
    AIConnection Save(AIConnection connection);

    void Remove(Guid connectionId);

    void SetDefault(Guid connectionId);
}
