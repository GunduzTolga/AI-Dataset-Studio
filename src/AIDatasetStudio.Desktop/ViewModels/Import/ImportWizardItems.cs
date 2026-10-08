using System.Globalization;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.ViewModels.Import;

/// <summary>
/// The dataset that receives the imported records.
/// </summary>
public sealed record ImportTarget(Guid DatasetId, string Name, DatasetType Type);

public sealed record DelimiterOption(char Value, string Label);

public sealed record EncodingOption(string Name, string Label);

/// <summary>
/// A source column in a mapping list; <see cref="Column"/> is <c>null</c> for "not mapped".
/// </summary>
public sealed record ColumnOption(string? Column, string Label);

/// <summary>
/// Which source column feeds one field of the target payload.
/// </summary>
public sealed partial class ColumnMappingViewModel(TargetFieldInfo target, string label, IReadOnlyList<ColumnOption> options) : ViewModelBase
{
    public TargetField Field => target.Field;

    public bool IsRequired => target.IsRequired;

    public string Label { get; } = label;

    public IReadOnlyList<ColumnOption> Options { get; } = options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    public partial ColumnOption? SelectedColumn { get; set; }

    /// <summary>
    /// A required field without a column.
    /// </summary>
    public bool IsMissing => IsRequired && SelectedColumn?.Column is null;
}

/// <summary>
/// One converted row in the preview, or a skipped row with its reason.
/// </summary>
public sealed class ImportRowViewModel
{
    private ImportRowViewModel(long number, bool isValid, string primary, string? secondary, string? badge)
    {
        Number = number;
        IsValid = isValid;
        Primary = primary;
        Secondary = secondary;
        Badge = badge;
    }

    public long Number { get; }

    public string NumberText => Number.ToString("N0", CultureInfo.CurrentCulture);

    public bool IsValid { get; }

    public bool IsInvalid => !IsValid;

    /// <summary>
    /// The question side of the record, or the error message.
    /// </summary>
    public string Primary { get; }

    /// <summary>
    /// The answer side of the record, if any.
    /// </summary>
    public string? Secondary { get; }

    public bool HasSecondary => !string.IsNullOrEmpty(Secondary);

    /// <summary>
    /// Extra information such as the number of messages in a conversation.
    /// </summary>
    public string? Badge { get; }

    public bool HasBadge => Badge is not null;

    public static ImportRowViewModel From(ImportRowResult row, Localizer localizer) =>
        row.Payload is { } payload ? FromPayload(row.RowNumber, payload, localizer) : FromError(row.Error!, localizer);

    public static ImportRowViewModel FromError(ImportRowError error, Localizer localizer) =>
        new(error.RowNumber, isValid: false, ImportText.Error(localizer, error), secondary: null, badge: null);

    private static ImportRowViewModel FromPayload(long number, RecordPayload payload, Localizer localizer)
    {
        string Text(string? value) => string.IsNullOrWhiteSpace(value) ? localizer["Field_Empty"] : Flatten(value);

        return payload switch
        {
            InstructionPayload p => new(number, true, Text(p.Instruction), Text(p.Output), badge: null),
            ChatPayload p => new(
                number,
                true,
                Text(p.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content),
                Text(p.Messages.FirstOrDefault(m => m.Role == ChatRole.Assistant)?.Content),
                p.Messages.Count > 2 ? localizer.Format("Import_MessageCount", p.Messages.Count) : null),
            PreferencePayload p => new(number, true, Text(p.Prompt), Text(p.Chosen), badge: null),
            _ => new(number, true, Text(payload.GetPreviewText()), secondary: null, badge: null),
        };
    }

    // Previews are single lines; long texts are trimmed by the view.
    private static string Flatten(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}

/// <summary>
/// Localized names for import enums.
/// </summary>
internal static class ImportText
{
    public static string Format(Localizer localizer, ImportFormat format) => localizer[$"ImportFormat_{format}"];

    public static string Layout(Localizer localizer, SourceLayout layout) => localizer[$"Layout_{layout}"];

    public static string Error(Localizer localizer, ImportRowError error) =>
        localizer.Format($"ImportError_{error.Code}", error.Detail);

    public static string Delimiter(Localizer localizer, char delimiter) => delimiter switch
    {
        ',' => localizer["Delimiter_Comma"],
        ';' => localizer["Delimiter_Semicolon"],
        '\t' => localizer["Delimiter_Tab"],
        '|' => localizer["Delimiter_Pipe"],
        _ => delimiter.ToString(),
    };

    public static string Encoding(Localizer localizer, string name) => name switch
    {
        CsvOptions.Utf8 => localizer["Encoding_Utf8"],
        CsvOptions.Windows1254 => localizer["Encoding_Windows1254"],
        CsvOptions.Iso88599 => localizer["Encoding_Iso88599"],
        CsvOptions.Windows1252 => localizer["Encoding_Windows1252"],
        CsvOptions.Utf16 => localizer["Encoding_Utf16"],
        CsvOptions.Utf16BigEndian => localizer["Encoding_Utf16BigEndian"],
        _ => name,
    };

    public static string FileError(Localizer localizer, ImportFileException exception)
    {
        var name = Path.GetFileName(exception.FilePath);
        return exception.Error switch
        {
            ImportFileError.NotFound => localizer.Format("Error_ImportFileNotFound", name),
            ImportFileError.InvalidJson => localizer.Format("Error_ImportInvalidJson", name, exception.Line),
            _ => localizer.Format("Error_ImportFileUnreadable", name, exception.InnerException?.Message),
        };
    }
}
