using System.Globalization;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// A conversation in which the assistant calls functions (SDD §4.4), for training function
/// calling and agents: the tools it may call, and turns whose calls carry what the function
/// returned. The answer that uses the results is the next assistant turn.
/// </summary>
public sealed record ToolCallingPayload : RecordPayload
{
    /// <param name="tools">Each tool as a JSON function definition: <c>{"name", "description", "parameters"}</c>.</param>
    /// <param name="messages">User and assistant turns; an assistant turn may call tools.</param>
    public ToolCallingPayload(string? systemPrompt, IReadOnlyList<string>? tools, IReadOnlyList<ChatMessage>? messages)
    {
        SystemPrompt = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt;
        Tools = tools is null ? [] : [.. tools.Select(t => t?.Trim() ?? string.Empty)];
        Messages = messages is null ? [] : [.. messages];
    }

    public override DatasetType Type => DatasetType.ToolCalling;

    public string? SystemPrompt { get; }

    /// <summary>
    /// The functions the assistant may call, each a JSON object as in OpenAI's <c>tools</c>
    /// (without the <c>{"type": "function"}</c> wrapper).
    /// </summary>
    public IReadOnlyList<string> Tools { get; }

    public IReadOnlyList<ChatMessage> Messages { get; }

    // Records compare collections by reference; compare them by value instead.
    public bool Equals(ToolCallingPayload? other) =>
        other is not null
        && SystemPrompt == other.SystemPrompt
        && Tools.SequenceEqual(other.Tools)
        && Messages.SequenceEqual(other.Messages);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SystemPrompt);
        foreach (var tool in Tools)
        {
            hash.Add(tool);
        }

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

        foreach (var tool in Tools)
        {
            yield return tool;
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

    // The tool count keeps the tools apart from the turns.
    protected override IEnumerable<string?> GetCanonicalParts()
    {
        yield return SystemPrompt;
        yield return Tools.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var tool in Tools)
        {
            yield return tool;
        }

        foreach (var part in Messages.SelectMany(m => m.GetCanonicalParts()))
        {
            yield return part;
        }
    }
}
