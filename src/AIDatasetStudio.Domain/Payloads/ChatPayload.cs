using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// Multi-turn conversation record (SDD §4.2).
/// </summary>
public sealed record ChatPayload : RecordPayload
{
    /// <exception cref="ArgumentException">A turn calls a function: that is a tool calling record (SDD §4.4).</exception>
    public ChatPayload(string? systemPrompt, IReadOnlyList<ChatMessage>? messages)
    {
        SystemPrompt = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt;
        Messages = messages is null ? [] : [.. messages];
        if (Messages.Any(m => m.ToolCalls.Count > 0))
        {
            throw new ArgumentException("A chat turn cannot call a function; use a tool calling record.", nameof(messages));
        }
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

        foreach (var text in Messages.SelectMany(m => m.GetSearchableTexts()))
        {
            yield return text;
        }
    }

    protected override string? GetPreviewSource() =>
        Messages.FirstOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content))?.Content
        ?? Messages.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Content))?.Content
        ?? SystemPrompt;

    protected override IEnumerable<string?> GetCanonicalParts()
    {
        yield return SystemPrompt;
        foreach (var part in Messages.SelectMany(m => m.GetCanonicalParts()))
        {
            yield return part;
        }
    }
}
