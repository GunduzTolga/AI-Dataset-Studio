using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// Pairwise preference record (SDD §4.3) for DPO / ORPO / RLHF.
/// </summary>
public sealed record PreferencePayload : RecordPayload
{
    public PreferencePayload(string? prompt, string? chosen, string? rejected)
    {
        Prompt = prompt ?? string.Empty;
        Chosen = chosen ?? string.Empty;
        Rejected = rejected ?? string.Empty;
    }

    public override DatasetType Type => DatasetType.Preference;

    public string Prompt { get; }

    public string Chosen { get; }

    public string Rejected { get; }

    protected override IEnumerable<string?> GetCanonicalParts() => [Prompt, Chosen, Rejected];
}
