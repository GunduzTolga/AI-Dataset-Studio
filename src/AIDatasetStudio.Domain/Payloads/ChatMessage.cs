using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

public sealed record ChatMessage
{
    /// <param name="reasoning">The thinking an assistant turn shows before its answer (SDD §4.2); kept
    /// only on assistant turns and when not blank.</param>
    /// <param name="toolCalls">The functions an assistant turn calls (SDD §4.4); kept only on assistant turns.</param>
    public ChatMessage(ChatRole role, string? content, string? reasoning = null, IReadOnlyList<ToolCall>? toolCalls = null)
    {
        Role = role;
        Content = content ?? string.Empty;
        Reasoning = role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(reasoning) ? reasoning : null;
        ToolCalls = role == ChatRole.Assistant && toolCalls is { Count: > 0 } ? [.. toolCalls] : [];
    }

    public ChatRole Role { get; }

    public string Content { get; }

    /// <summary>
    /// The model's reasoning (thinking) before the answer, for training reasoning models; <c>null</c> for none.
    /// </summary>
    public string? Reasoning { get; }

    /// <summary>
    /// The functions the turn calls, in order; empty for a turn that only writes.
    /// </summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; }

    // Records compare collections by reference; compare the calls by value instead.
    public bool Equals(ChatMessage? other) =>
        other is not null
        && Role == other.Role
        && Content == other.Content
        && Reasoning == other.Reasoning
        && ToolCalls.SequenceEqual(other.ToolCalls);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Role);
        hash.Add(Content);
        hash.Add(Reasoning);
        foreach (var call in ToolCalls)
        {
            hash.Add(call);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// The turn's parts for the content hash. A turn without reasoning or calls has the parts of
    /// earlier versions, so stored hashes stay valid. Content is never null, so a null part marks
    /// tagged extras of fixed length ("reasoning" + text, "call" + name + arguments + result);
    /// the next turn starts with its role, which no tag equals.
    /// </summary>
    internal IEnumerable<string?> GetCanonicalParts()
    {
        yield return Role.ToString();
        yield return Content;
        if (Reasoning is null && ToolCalls.Count == 0)
        {
            yield break;
        }

        yield return null;
        if (Reasoning is { } reasoning)
        {
            yield return "reasoning";
            yield return reasoning;
        }

        foreach (var call in ToolCalls)
        {
            yield return "call";
            yield return call.Name;
            yield return call.Arguments;
            yield return call.Result;
        }
    }

    /// <summary>
    /// The turn's texts for search: reasoning, content, then each call and its result.
    /// </summary>
    internal IEnumerable<string> GetSearchableTexts()
    {
        if (Reasoning is { } reasoning)
        {
            yield return reasoning;
        }

        yield return Content;
        foreach (var call in ToolCalls)
        {
            yield return call.Name;
            yield return call.Arguments;
            if (call.Result is { } result)
            {
                yield return result;
            }
        }
    }
}
