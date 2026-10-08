using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

public sealed record ChatMessage
{
    public ChatMessage(ChatRole role, string? content)
    {
        Role = role;
        Content = content ?? string.Empty;
    }

    public ChatRole Role { get; }

    public string Content { get; }
}
