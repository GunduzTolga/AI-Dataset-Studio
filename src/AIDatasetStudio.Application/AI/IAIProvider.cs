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
    /// Adds the connection or replaces the one with the same id. The first one becomes the default.
    /// </summary>
    void Save(AIConnection connection);

    void Remove(Guid connectionId);

    void SetDefault(Guid connectionId);
}
