using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Desktop.Localization;

namespace AIDatasetStudio.Desktop.ViewModels.AI;

/// <summary>
/// Localized names and error messages of AI connections.
/// </summary>
internal static class AIText
{
    public static string Provider(Localizer localizer, AIProviderKind kind) => localizer[$"AIProvider_{kind}"];

    /// <summary>
    /// "qwen2.5:7b · Ollama".
    /// </summary>
    public static string Describe(AIConnection connection) =>
        string.IsNullOrWhiteSpace(connection.Model) ? connection.Name : $"{connection.Model} · {connection.Name}";

    public static string Error(Localizer localizer, AIException exception, AIConnection connection) => exception.Kind switch
    {
        AIErrorKind.InvalidUrl => localizer.Format("AIError_InvalidUrl", connection.BaseUrl),
        AIErrorKind.RemoteNotAllowed => localizer["AIError_RemoteNotAllowed"],
        AIErrorKind.Unreachable => localizer.Format("AIError_Unreachable", connection.BaseUrl, Provider(localizer, connection.Kind)),
        AIErrorKind.Timeout => localizer.Format("AIError_Timeout", connection.BaseUrl),
        AIErrorKind.Unauthorized => localizer["AIError_Unauthorized"],
        AIErrorKind.ModelNotFound => localizer.Format("AIError_ModelNotFound", connection.Model),
        AIErrorKind.NoEmbeddingModel => localizer.Format("AIError_NoEmbeddingModel", connection.Name),
        AIErrorKind.EmbeddingModelNotFound => localizer.Format("AIError_EmbeddingModelNotFound", connection.EmbeddingModel),
        AIErrorKind.NotAnApi => localizer.Format("AIError_NotAnApi", connection.BaseUrl),
        AIErrorKind.Rejected => localizer.Format("AIError_Rejected", exception.Detail ?? exception.Message),
        _ => exception.Detail is { } detail ? localizer.Format("AIError_ServerErrorDetail", detail) : localizer["AIError_ServerError"],
    };
}
