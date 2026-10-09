using System.Net;
using System.Net.Sockets;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// The model servers the app can talk to (SDD §17.1). All but Anthropic serve the OpenAI chat
/// completions API; they differ in their default address and how the API key is sent.
/// </summary>
/// <remarks>
/// Values are persisted by name in the user settings; never rename existing members.
/// </remarks>
public enum AIProviderKind
{
    Ollama,
    LMStudio,
    Vllm,
    LocalAI,

    /// <summary>Any other server with an OpenAI-compatible API.</summary>
    OpenAICompatible,

    /// <summary>OpenAI's cloud API (M19).</summary>
    OpenAI,

    /// <summary>Anthropic's Messages API; the only one that is not OpenAI-compatible.</summary>
    Anthropic,

    /// <summary>A resource of Azure OpenAI, through its OpenAI-compatible v1 API; the key goes in an <c>api-key</c> header.</summary>
    AzureOpenAI,

    OpenRouter,
    Groq,
    DeepSeek,
}

/// <summary>
/// A model server and the model used on it. Connections are kept in the user settings, not in
/// workspace files: servers belong to a machine, datasets travel.
/// </summary>
/// <param name="BaseUrl">The API root, e.g. "http://localhost:11434/v1".</param>
/// <param name="ApiKey">Sent as a bearer token when set; most local servers need none.</param>
/// <param name="MaxTokens">The longest answer to ask for; <c>null</c> leaves it to the server.</param>
/// <param name="AllowRemote">The user agreed that records may be sent to a server outside this
/// computer and its local network (SDD §2, zero cloud dependency).</param>
/// <param name="EmbeddingModel">The model that turns questions into vectors to find questions
/// alike in meaning (SDD §8.2, e.g. "bge-m3"); <c>null</c> when none is chosen.</param>
/// <param name="HasStoredKey">The API key is kept in the operating system's key store (M19)
/// rather than in <paramref name="ApiKey"/>, which is then <c>null</c>.</param>
public sealed record AIConnection(
    Guid Id,
    string Name,
    AIProviderKind Kind,
    string BaseUrl,
    string Model,
    string? ApiKey = null,
    double Temperature = AIConnection.DefaultTemperature,
    int? MaxTokens = AIConnection.DefaultMaxTokens,
    bool AllowRemote = false,
    string? EmbeddingModel = null,
    bool HasStoredKey = false)
{
    public const double DefaultTemperature = 0.7;
    public const int DefaultMaxTokens = 2_048;

    /// <summary>
    /// Whether requests leave this computer and its local network.
    /// </summary>
    public bool IsRemote => !AIProviders.IsLocal(BaseUrl);
}

/// <summary>
/// One message sent to a model.
/// </summary>
public sealed record AIChatMessage(ChatRole Role, string Content);

public sealed record AIChatRequest(IReadOnlyList<AIChatMessage> Messages, double Temperature, int? MaxTokens);

public static class AIProviders
{
    /// <summary>
    /// The address each server listens on after a default installation.
    /// </summary>
    public static string DefaultUrl(AIProviderKind kind) => kind switch
    {
        AIProviderKind.Ollama => "http://localhost:11434/v1",
        AIProviderKind.LMStudio => "http://localhost:1234/v1",
        AIProviderKind.Vllm => "http://localhost:8000/v1",
        AIProviderKind.LocalAI => "http://localhost:8080/v1",
        AIProviderKind.OpenAI => "https://api.openai.com/v1",
        AIProviderKind.Anthropic => "https://api.anthropic.com/v1",

        // Not an address yet: the user puts in the name of their resource.
        AIProviderKind.AzureOpenAI => "https://<resource>.openai.azure.com/openai/v1",
        AIProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
        AIProviderKind.Groq => "https://api.groq.com/openai/v1",
        AIProviderKind.DeepSeek => "https://api.deepseek.com/v1",
        _ => "http://localhost:8080/v1",
    };

    /// <summary>
    /// A model service run by a company on the internet, rather than a server the user runs.
    /// Records sent to it are subject to the company's terms; it always needs an API key.
    /// </summary>
    public static bool IsCloud(AIProviderKind kind) =>
        kind is AIProviderKind.OpenAI or AIProviderKind.Anthropic or AIProviderKind.AzureOpenAI
            or AIProviderKind.OpenRouter or AIProviderKind.Groq or AIProviderKind.DeepSeek;

    /// <summary>
    /// The environment variable the service's own tools read the API key from, used when the
    /// connection has no key of its own (e.g. <c>OPENAI_API_KEY</c>); <c>null</c> for servers
    /// people run themselves.
    /// </summary>
    public static string? KeyVariable(AIProviderKind kind) => kind switch
    {
        AIProviderKind.OpenAI => "OPENAI_API_KEY",
        AIProviderKind.Anthropic => "ANTHROPIC_API_KEY",
        AIProviderKind.AzureOpenAI => "AZURE_OPENAI_API_KEY",
        AIProviderKind.OpenRouter => "OPENROUTER_API_KEY",
        AIProviderKind.Groq => "GROQ_API_KEY",
        AIProviderKind.DeepSeek => "DEEPSEEK_API_KEY",
        _ => null,
    };

    /// <summary>
    /// Whether the service can turn texts into vectors (SDD §8.2). Anthropic, Groq and DeepSeek
    /// offer no embeddings API.
    /// </summary>
    public static bool OffersEmbeddings(AIProviderKind kind) =>
        kind is not (AIProviderKind.Anthropic or AIProviderKind.Groq or AIProviderKind.DeepSeek);

    /// <summary>
    /// The name of a connection's API key in the operating system's key store.
    /// </summary>
    public static string SecretName(Guid connectionId) => $"ai-connection/{connectionId:D}";

    /// <summary>
    /// An absolute http(s) address without a trailing slash, or <c>null</c> if it is not one.
    /// </summary>
    public static Uri? ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri;
    }

    /// <summary>
    /// Whether an address is on this computer or the local network: loopback, private and
    /// link-local IP addresses, and host names without a dot or under .local, .lan, .internal
    /// or .home.arpa. Anything else is remote and needs the user's consent.
    /// </summary>
    public static bool IsLocal(string? url)
    {
        if (ParseUrl(url) is not { } uri)
        {
            // Nothing is sent to an address that cannot be parsed.
            return true;
        }

        if (uri.IsLoopback)
        {
            return true;
        }

        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address))
        {
            return IsPrivate(address);
        }

        var host = uri.IdnHost.TrimEnd('.');
        return !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 unique local, fe80::/10 link-local.
            var first = address.GetAddressBytes()[0];
            return (first & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }
}
