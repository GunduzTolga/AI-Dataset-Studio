using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Target formats of an export (SDD §13.1). All are JSON Lines: one record per line.
/// </summary>
public enum ExportFormat
{
    /// <summary>OpenAI fine-tuning: <c>{"messages": [...]}</c>; preference pairs in OpenAI's DPO shape.</summary>
    OpenAi,

    /// <summary><c>{"conversations": [{"from": "human", "value": ...}, ...]}</c>.</summary>
    ShareGpt,

    /// <summary><c>{"instruction": ..., "input": ..., "output": ...}</c>; chats only with one question and one answer.</summary>
    Alpaca,

    /// <summary>Hugging Face TRL preference data: <c>{"prompt": ..., "chosen": ..., "rejected": ...}</c>.</summary>
    HuggingFaceDpo,

    /// <summary>The conversation as ChatML text: <c>{"text": "&lt;|im_start|&gt;user\n...&lt;|im_end|&gt;\n..."}</c>.</summary>
    ChatMl,

    /// <summary>The conversation as Llama 3 Instruct text: <c>{"text": "&lt;|begin_of_text|&gt;..."}</c>.</summary>
    Llama3,
}

/// <summary>
/// The files of a train / validation / test split (SDD §10).
/// </summary>
public enum SplitPart
{
    Train,
    Validation,
    Test,
}

/// <summary>
/// Shares of a hash-based split (SDD §10.1, ADR-018).
/// </summary>
/// <param name="TrainPercent">Share of the training file, 1–100.</param>
/// <param name="ValidationPercent">Share of the validation file, 0–99.</param>
/// <param name="TestPercent">Share of the test file, 0–99. The three add up to 100.</param>
/// <param name="Seed">The same seed always puts the same records in the same files.</param>
/// <param name="StratifyBy">
/// Splits each tag stratum on its own with exact shares (SDD §10.1): <c>null</c> for no strata,
/// <see cref="AnyTag"/> for the record's first tag, or a tag group such as "kategori".
/// </param>
public sealed record SplitOptions(int TrainPercent, int ValidationPercent, int TestPercent, int Seed = SplitOptions.DefaultSeed, string? StratifyBy = null)
{
    public const int DefaultSeed = 42;

    /// <summary>
    /// <see cref="StratifyBy"/> value for "by tag": a record's stratum is its first tag by name.
    /// </summary>
    public const string AnyTag = "";

    public bool IsStratified => StratifyBy is not null;

    /// <summary>80 / 10 / 10, the usual choice.</summary>
    public static SplitOptions Standard { get; } = new(80, 10, 10);

    /// <summary>90 / 10 without a test file, for large datasets.</summary>
    public static SplitOptions TrainValidation { get; } = new(90, 10, 0);

    public bool IsValid =>
        TrainPercent is > 0 and <= 100 && ValidationPercent is >= 0 and < 100 && TestPercent is >= 0 and < 100
        && TrainPercent + ValidationPercent + TestPercent == 100;

    public int PercentOf(SplitPart part) => part switch
    {
        SplitPart.Train => TrainPercent,
        SplitPart.Validation => ValidationPercent,
        _ => TestPercent,
    };

    /// <summary>
    /// The parts that get a file: those with a share above zero.
    /// </summary>
    public IReadOnlyList<SplitPart> Parts => [.. Enum.GetValues<SplitPart>().Where(part => PercentOf(part) > 0)];
}

/// <summary>
/// What to export and how (the export profile, SDD §13.2).
/// </summary>
/// <param name="Statuses">Records with these statuses are exported; archived records never are.</param>
/// <param name="SkipOverContextLimit">Leave out records longer than the dataset's context length.</param>
/// <param name="SkipDuplicates">Export only the first of records with exactly the same content.</param>
/// <param name="MaskPersonalData">Replace personal data with placeholders such as <c>[EMAIL_1]</c> (SDD §9).</param>
/// <param name="Split">Write train / validation / test files; <c>null</c> for a single file.</param>
/// <param name="Training">Also write a fine-tuning configuration for the files (M20); <c>null</c> for none.</param>
public sealed record ExportOptions(
    ExportFormat Format,
    IReadOnlyList<RecordStatus> Statuses,
    bool SkipOverContextLimit = true,
    bool SkipDuplicates = true,
    bool MaskPersonalData = true,
    SplitOptions? Split = null,
    TrainingOptions? Training = null)
{
    /// <summary>
    /// Approved records only, the safe default (SDD §13.2).
    /// </summary>
    public static ExportOptions DefaultFor(ExportFormat format) => new(format, [RecordStatus.Approved]);

    // Records compare collections by reference; compare the statuses by value instead.
    public bool Equals(ExportOptions? other) =>
        other is not null
        && Format == other.Format
        && Statuses.SequenceEqual(other.Statuses)
        && SkipOverContextLimit == other.SkipOverContextLimit
        && SkipDuplicates == other.SkipDuplicates
        && MaskPersonalData == other.MaskPersonalData
        && Split == other.Split
        && Training == other.Training;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Format);
        foreach (var status in Statuses)
        {
            hash.Add(status);
        }

        hash.Add(SkipOverContextLimit);
        hash.Add(SkipDuplicates);
        hash.Add(MaskPersonalData);
        hash.Add(Split);
        hash.Add(Training);
        return hash.ToHashCode();
    }
}

/// <summary>
/// What an export with given options would write, before writing it.
/// </summary>
/// <param name="Selected">Records with a chosen status.</param>
/// <param name="Unsupported">Of those, records the format cannot represent (e.g. long chats in Alpaca).</param>
/// <param name="OverContextLimit">Records left out for their length.</param>
/// <param name="Duplicates">Later copies left out.</param>
/// <param name="NotAnalyzed">Records whose length is not known yet; they are checked when exporting.</param>
/// <param name="SampleLines">The first lines of the file, exactly as they will be written.</param>
public sealed record ExportPreview(
    long Selected,
    long Unsupported,
    long OverContextLimit,
    long Duplicates,
    long NotAnalyzed,
    IReadOnlyList<string> SampleLines)
{
    public long Exported => Selected - Unsupported - OverContextLimit - Duplicates;
}

public sealed record ExportedFile(SplitPart? Part, string Path, long Count);

/// <summary>
/// A finished export.
/// </summary>
/// <param name="Masked">Records in which personal data was replaced.</param>
/// <param name="TrainingFiles">The fine-tuning configuration written next to the data, if one was asked for.</param>
public sealed record ExportResult(
    IReadOnlyList<ExportedFile> Files,
    long Exported,
    long Unsupported,
    long OverContextLimit,
    long Duplicates,
    long Masked,
    IReadOnlyList<string>? TrainingFiles = null);

/// <param name="Processed">Records read so far.</param>
/// <param name="Total">Records with a chosen status.</param>
public readonly record struct ExportProgress(long Processed, long Total);

/// <summary>
/// An earlier export of a dataset (SDD §18 ExportHistories).
/// </summary>
/// <param name="VersionNumber">"1.2.0" when a version was exported; <c>null</c> for the dataset as it was.</param>
public sealed record ExportHistoryEntry(ExportFormat Format, long RecordCount, IReadOnlyList<string> Files, DateTimeOffset ExportedAt, string? VersionNumber = null);
