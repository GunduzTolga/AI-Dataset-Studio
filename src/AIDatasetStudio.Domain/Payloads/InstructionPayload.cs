using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Payloads;

/// <summary>
/// Single-turn instruction record (SDD §4.1), used for SFT / LoRA style training.
/// </summary>
public sealed record InstructionPayload : RecordPayload
{
    public InstructionPayload(string? instruction, string? input, string? output)
    {
        Instruction = instruction ?? string.Empty;
        Input = input ?? string.Empty;
        Output = output ?? string.Empty;
    }

    public override DatasetType Type => DatasetType.Instruction;

    public string Instruction { get; }

    public string Input { get; }

    public string Output { get; }

    protected override IEnumerable<string?> GetCanonicalParts() => [Instruction, Input, Output];

    public override IEnumerable<string> GetSearchableTexts() => [Instruction, Input, Output];

    protected override string? GetPreviewSource() =>
        FirstNonBlank(Instruction, Input, Output);

    private static string? FirstNonBlank(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
