using System.Runtime.CompilerServices;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// The default AI connection and writing with it (SDD §17): the editor and the dataset overview
/// go through here.
/// </summary>
public sealed class AIAssistant
{
    private readonly IAIConnectionStore _store;
    private readonly IAIProviderFactory _providers;
    private readonly Localizer _localizer;

    public AIAssistant(IAIConnectionStore store, IAIProviderFactory providers, Localizer localizer)
    {
        _store = store;
        _providers = providers;
        _localizer = localizer;
        _store.Changed += (_, _) => ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when connections change, e.g. another default is chosen.
    /// </summary>
    public event EventHandler? ConnectionChanged;

    /// <summary>
    /// The connection used for writing; <c>null</c> until one is set up.
    /// </summary>
    public AIConnection? Connection => _store.DefaultConnection;

    public AIConnectionsViewModel CreateConnections() => new(_store, _providers, _localizer);

    /// <summary>
    /// Sends <paramref name="messages"/> to the connection's model and yields its answer piece by
    /// piece, without reasoning blocks.
    /// </summary>
    /// <param name="temperature">Instead of the connection's temperature, e.g. 0 for a judgment.</param>
    /// <exception cref="AIException">The server cannot be used.</exception>
    public async IAsyncEnumerable<string> WriteAsync(
        AIConnection connection,
        IReadOnlyList<AIChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        double? temperature = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var request = new AIChatRequest(messages, temperature ?? connection.Temperature, connection.MaxTokens);
        var provider = _providers.Create(connection);
        await foreach (var piece in ReasoningFilter.ApplyAsync(provider.StreamChatAsync(request, cancellationToken), cancellationToken))
        {
            yield return piece;
        }
    }

    /// <summary>
    /// The messages that ask for <paramref name="target"/>, or <c>null</c> when the record has
    /// nothing to answer yet.
    /// </summary>
    public static IReadOnlyList<AIChatMessage>? Prompt(RecordPayload payload, AIWriteTarget target, string? guidance) =>
        AIPrompts.For(payload, target, guidance);

    /// <summary>
    /// A server error as the user should read it.
    /// </summary>
    public string Describe(AIException exception, AIConnection connection) => AIText.Error(_localizer, exception, connection);
}
