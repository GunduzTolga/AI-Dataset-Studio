using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// Single-turn instruction record (SDD §4.1), used for SFT / LoRA style training.
/// </summary>
public sealed record InstructionPayload : RecordPayload
{
    /// <param name="reasoning">The thinking shown before the output (SDD §4.1); kept only when not blank.</param>
    public InstructionPayload(string? instruction, string? input, string? output, string? reasoning = null)
    {
        Instruction = instruction ?? string.Empty;
        Input = input ?? string.Empty;
        Output = output ?? string.Empty;
        Reasoning = string.IsNullOrWhiteSpace(reasoning) ? null : reasoning;
    }

    public override DatasetType Type => DatasetType.Instruction;

    public string Instruction { get; }

    public string Input { get; }

    public string Output { get; }

    /// <summary>
    /// The model's reasoning (thinking) before the output, for training reasoning models; <c>null</c> for none.
    /// </summary>
    public string? Reasoning { get; }

    // Without reasoning the parts are those of earlier versions, so stored hashes stay valid;
    // the null part marks what follows as the reasoning.
    protected override IEnumerable<string?> GetCanonicalParts() =>
        Reasoning is null ? [Instruction, Input, Output] : [Instruction, Input, Output, null, Reasoning];

    public override IEnumerable<string> GetSearchableTexts() =>
        Reasoning is null ? [Instruction, Input, Output] : [Instruction, Input, Output, Reasoning];

    protected override string? GetPreviewSource() =>
        FirstNonBlank(Instruction, Input, Output);

    private static string? FirstNonBlank(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
