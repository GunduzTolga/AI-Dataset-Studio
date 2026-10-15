using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

public sealed record ChatMessage
{
    /// <param name="reasoning">The thinking an assistant turn shows before its answer (SDD §4.2); kept
    /// only on assistant turns and when not blank.</param>
    public ChatMessage(ChatRole role, string? content, string? reasoning = null)
    {
        Role = role;
        Content = content ?? string.Empty;
        Reasoning = role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(reasoning) ? reasoning : null;
    }

    public ChatRole Role { get; }

    public string Content { get; }

    /// <summary>
    /// The model's reasoning (thinking) before the answer, for training reasoning models; <c>null</c> for none.
    /// </summary>
    public string? Reasoning { get; }
}
