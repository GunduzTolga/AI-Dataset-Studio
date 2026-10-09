namespace AIDatasetStudio.Application.AI;

public enum AIErrorKind
{
    /// <summary>The address is not a valid http(s) URL.</summary>
    InvalidUrl,

    /// <summary>The address is outside this computer and the user has not allowed sending records there.</summary>
    RemoteNotAllowed,

    /// <summary>Nothing answers at the address (server not running, wrong port or host).</summary>
    Unreachable,

    /// <summary>The server stopped answering.</summary>
    Timeout,

    /// <summary>The server wants a (different) API key.</summary>
    Unauthorized,

    /// <summary>The server does not have the model.</summary>
    ModelNotFound,

    /// <summary>The address answers, but not with an OpenAI-compatible API (often a missing "/v1").</summary>
    NotAnApi,

    /// <summary>The server refused the request; <see cref="AIException.Detail"/> says why.</summary>
    Rejected,

    /// <summary>The server failed; <see cref="AIException.Detail"/> says why.</summary>
    ServerError,

    /// <summary>The connection has no embedding model chosen.</summary>
    NoEmbeddingModel,

    /// <summary>The server does not have the embedding model, or it cannot make vectors.</summary>
    EmbeddingModelNotFound,
}

/// <summary>
/// A model server could not be used. <see cref="Exception.Message"/> is English text for logs;
/// interfaces show localized text based on <see cref="Kind"/>.
/// </summary>
/// <param name="detail">The server's own explanation, if it gave one.</param>
public sealed class AIException(AIErrorKind kind, string message, string? detail = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public AIErrorKind Kind { get; } = kind;

    public string? Detail { get; } = detail;
}
