using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Settings;

namespace AIDatasetStudio.Infrastructure.AI;

/// <summary>
/// Creates providers that share one connection pool, refusing remote addresses the user has not
/// allowed. The API key comes from the connection, the key store or the service's environment
/// variable, in that order (ADR-041).
/// </summary>
/// <param name="secrets">The key store holding keys of connections with <see cref="AIConnection.HasStoredKey"/>.</param>
internal sealed class AIProviderFactory(ISecretStore? secrets = null) : IAIProviderFactory, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // Answers can take minutes; the provider watches for silence instead.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public IAIProvider Create(AIConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (AIProviders.ParseUrl(connection.BaseUrl) is null)
        {
            throw new AIException(AIErrorKind.InvalidUrl, $"'{connection.BaseUrl}' is not an http(s) address.");
        }

        if (connection.IsRemote && !connection.AllowRemote)
        {
            throw new AIException(AIErrorKind.RemoteNotAllowed, $"Sending records to '{connection.BaseUrl}' has not been allowed.");
        }

        var key = ApiKey(connection);
        if (key is null && AIProviders.IsCloud(connection.Kind))
        {
            throw new AIException(AIErrorKind.NoApiKey, $"{connection.Kind} needs an API key.");
        }

        return connection.Kind == AIProviderKind.Anthropic
            ? new AnthropicProvider(_http, connection, key)
            : new OpenAICompatibleProvider(_http, connection, key);
    }

    private string? ApiKey(AIConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            return connection.ApiKey;
        }

        var stored = connection.HasStoredKey ? secrets?.Read(AIProviders.SecretName(connection.Id)) : null;
        var fromEnvironment = AIProviders.KeyVariable(connection.Kind) is { } variable ? Environment.GetEnvironmentVariable(variable) : null;
        var key = string.IsNullOrWhiteSpace(stored) ? fromEnvironment : stored;
        if (string.IsNullOrWhiteSpace(key) && connection.HasStoredKey)
        {
            throw new AIException(AIErrorKind.ApiKeyUnreadable, "The API key could not be read from the key store.");
        }

        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public void Dispose() => _http.Dispose();
}
