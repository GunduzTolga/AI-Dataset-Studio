using System.Net;
using System.Net.Sockets;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.AI;

/// <summary>
/// The model servers the app can talk to (SDD §17.1). All of them serve the OpenAI chat
/// completions API; they differ in their default address.
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
    string? EmbeddingModel = null)
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
        _ => "http://localhost:8080/v1",
    };

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
