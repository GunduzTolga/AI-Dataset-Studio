namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// A function an assistant turn calls (SDD §4.4), with what the function returned.
/// </summary>
public sealed record ToolCall
{
    /// <param name="name">The function's name, as the tool definition names it.</param>
    /// <param name="arguments">The arguments as a JSON object, e.g. <c>{"location": "Kadıköy"}</c>.</param>
    /// <param name="result">What the function returned (often JSON); <c>null</c> when the example
    /// ends with the call, as in datasets that teach only which function to call.</param>
    public ToolCall(string? name, string? arguments, string? result = null)
    {
        // Text around the JSON is trimmed: it carries nothing and would not survive storage.
        Name = name?.Trim() ?? string.Empty;
        Arguments = arguments?.Trim() ?? string.Empty;
        Result = string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public string Name { get; }

    public string Arguments { get; }

    public string? Result { get; }
}
