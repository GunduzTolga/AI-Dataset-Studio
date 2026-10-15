using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Which formats fit which dataset types (the compatibility matrix, SDD §13.3) and file names.
/// </summary>
public static class ExportFormats
{
    public const string Extension = ".jsonl";

    /// <summary>
    /// The formats a dataset type can be exported to, most common first.
    /// </summary>
    public static IReadOnlyList<ExportFormat> For(DatasetType type) => type switch
    {
        DatasetType.Instruction => [ExportFormat.OpenAi, ExportFormat.Alpaca, ExportFormat.ShareGpt, ExportFormat.ChatMl, ExportFormat.Llama3],
        DatasetType.Chat => [ExportFormat.OpenAi, ExportFormat.ShareGpt, ExportFormat.ChatMl, ExportFormat.Llama3, ExportFormat.Alpaca],
        DatasetType.Preference => [ExportFormat.HuggingFaceDpo, ExportFormat.OpenAi],
        DatasetType.ToolCalling => [ExportFormat.OpenAi, ExportFormat.ShareGpt],
        _ => [],
    };

    /// <summary>
    /// Some records of this type cannot be written in this format and are left out (Alpaca has
    /// room for one question and one answer only).
    /// </summary>
    public static bool IsLossy(ExportFormat format, DatasetType type) => format == ExportFormat.Alpaca && type == DatasetType.Chat;

    /// <summary>
    /// Whether the record can be written in the format without losing content. A tool calling
    /// conversation must also be complete (<see cref="ToolCalls.IsWellFormed"/>).
    /// </summary>
    public static bool CanWrite(ExportFormat format, RecordPayload payload) => (format, payload) switch
    {
        (ExportFormat.OpenAi or ExportFormat.ShareGpt, ToolCallingPayload tools) => ToolCalls.IsWellFormed(tools),
        (_, ToolCallingPayload) => false,
        (ExportFormat.Alpaca, ChatPayload chat) => IsSingleTurn(chat),
        (ExportFormat.HuggingFaceDpo, not PreferencePayload) => false,
        (ExportFormat.ShareGpt or ExportFormat.Alpaca or ExportFormat.ChatMl or ExportFormat.Llama3, PreferencePayload) => false,
        _ => true,
    };

    /// <summary>
    /// One question and its answer: exactly a user turn followed by an assistant turn (a system
    /// prompt is allowed).
    /// </summary>
    public static bool IsSingleTurn(ChatPayload chat) =>
        chat.Messages is [{ Role: ChatRole.User }, { Role: ChatRole.Assistant }];

    /// <summary>
    /// File name of a split part: <c>train.jsonl</c>, <c>val.jsonl</c>, <c>test.jsonl</c> (SDD §10.1).
    /// </summary>
    public static string FileName(SplitPart part) => part switch
    {
        SplitPart.Train => "train" + Extension,
        SplitPart.Validation => "val" + Extension,
        _ => "test" + Extension,
    };

    /// <summary>
    /// The files an export writes: <paramref name="destination"/> itself for a single file, or one
    /// file per split part inside the <paramref name="destination"/> folder.
    /// </summary>
    public static IReadOnlyList<(SplitPart? Part, string Path)> Files(ExportOptions options, string destination) =>
        options.Split is { } split
            ? [.. split.Parts.Select(part => ((SplitPart?)part, Path.Combine(destination, FileName(part))))]
            : [(null, destination)];
}
