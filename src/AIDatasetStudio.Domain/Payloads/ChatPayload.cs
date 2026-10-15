using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// Multi-turn conversation record (SDD §4.2).
/// </summary>
public sealed record ChatPayload : RecordPayload
{
    public ChatPayload(string? systemPrompt, IReadOnlyList<ChatMessage>? messages)
    {
        SystemPrompt = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt;
        Messages = messages is null ? [] : [.. messages];
    }

    public override DatasetType Type => DatasetType.Chat;

    public string? SystemPrompt { get; }

    public IReadOnlyList<ChatMessage> Messages { get; }

    // Records compare collections by reference; compare the messages by value instead.
    public bool Equals(ChatPayload? other) =>
        other is not null
        && SystemPrompt == other.SystemPrompt
        && Messages.SequenceEqual(other.Messages);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SystemPrompt);
        foreach (var message in Messages)
        {
            hash.Add(message);
        }

        return hash.ToHashCode();
    }

    public override IEnumerable<string> GetSearchableTexts()
    {
        if (SystemPrompt is not null)
        {
            yield return SystemPrompt;
        }

        foreach (var message in Messages)
        {
            if (message.Reasoning is { } reasoning)
            {
                yield return reasoning;
            }

            yield return message.Content;
        }
    }

    protected override string? GetPreviewSource() =>
        Messages.FirstOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content))?.Content
        ?? Messages.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Content))?.Content
        ?? SystemPrompt;

    protected override IEnumerable<string?> GetCanonicalParts()
    {
        yield return SystemPrompt;
        foreach (var message in Messages)
        {
            yield return message.Role.ToString();
            yield return message.Content;

            // A turn without reasoning has the parts of earlier versions, so stored hashes stay
            // valid. Content is never null, so a null part marks a tagged extra; the next turn
            // starts with its role, which no tag equals.
            if (message.Reasoning is { } reasoning)
            {
                yield return null;
                yield return "reasoning";
                yield return reasoning;
            }
        }
    }
}
