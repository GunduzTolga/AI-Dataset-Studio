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
/// A way to read the file, such as a .txt file as a table or as paragraphs.
/// </summary>
public sealed record FormatOption(ImportFormat Value, string Label);

/// <summary>
/// A sheet of a workbook; <see cref="Name"/> is <c>null</c> for "all sheets".
/// </summary>
public sealed record SheetOption(string? Name, string Label);

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
    private ImportRowViewModel(long number, bool isValid, string primary, string? secondary, string? badge, string? sheet = null)
    {
        Number = number;
        IsValid = isValid;
        Primary = primary;
        Secondary = secondary;
        Badge = badge;
        Sheet = sheet;
    }

    public long Number { get; }

    /// <summary>
    /// The workbook sheet of the row, shown when every sheet is read.
    /// </summary>
    public string? Sheet { get; }

    public string NumberText => Sheet is null
        ? Number.ToString("N0", CultureInfo.CurrentCulture)
        : $"{Sheet} · {Number.ToString("N0", CultureInfo.CurrentCulture)}";

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

    /// <param name="showSheet">Whether to show the row's sheet with its number (rows of several sheets).</param>
    public static ImportRowViewModel From(ImportRowResult row, Localizer localizer, bool showSheet = false) =>
        row.Payload is { } payload
            ? FromPayload(row.RowNumber, payload, localizer, showSheet ? row.Sheet : null)
            : FromError(row.Error!, localizer, showSheet);

    public static ImportRowViewModel FromError(ImportRowError error, Localizer localizer, bool showSheet = false) =>
        new(error.RowNumber, isValid: false, ImportText.Error(localizer, error), secondary: null, badge: null, showSheet ? error.Sheet : null);

    private static ImportRowViewModel FromPayload(long number, RecordPayload payload, Localizer localizer, string? sheet)
    {
        string Text(string? value) => string.IsNullOrWhiteSpace(value) ? localizer["Field_Empty"] : Flatten(value);

        return payload switch
        {
            InstructionPayload p => new(number, true, Text(p.Instruction), Text(p.Output), badge: null, sheet),
            ChatPayload p => new(
                number,
                true,
                Text(p.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Content),
                Text(p.Messages.FirstOrDefault(m => m.Role == ChatRole.Assistant)?.Content),
                p.Messages.Count > 2 ? localizer.Format("Import_MessageCount", p.Messages.Count) : null,
                sheet),
            PreferencePayload p => new(number, true, Text(p.Prompt), Text(p.Chosen), badge: null, sheet),
            _ => new(number, true, Text(payload.GetPreviewText()), secondary: null, badge: null, sheet),
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

    /// <summary>
    /// A source column as the mapping lists show it: the columns of Markdown sections and text
    /// paragraphs have localized names, file columns keep their own.
    /// </summary>
    public static string Column(Localizer localizer, ImportFormat format, string column) =>
        format is ImportFormat.Markdown or ImportFormat.PlainText or ImportFormat.Pdf or ImportFormat.Word or ImportFormat.Html or ImportFormat.Archive
            ? localizer[$"ImportColumn_{column}"]
            : column;

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
            ImportFileError.LegacyWorkbook => localizer.Format("Error_ImportLegacyWorkbook", name),
            ImportFileError.InvalidWorkbook => localizer.Format("Error_ImportInvalidWorkbook", name),
            ImportFileError.SheetNotFound => localizer.Format("Error_ImportSheetNotFound", name),
            ImportFileError.LegacyDocument => localizer.Format("Error_ImportLegacyDocument", name),
            ImportFileError.InvalidDocument => localizer.Format("Error_ImportInvalidDocument", name),
            ImportFileError.ProtectedPdf => localizer.Format("Error_ImportProtectedPdf", name),
            ImportFileError.NoText => localizer.Format("Error_ImportNoText", name),
            ImportFileError.EmptyArchive => localizer.Format("Error_ImportEmptyArchive", name),
            ImportFileError.InvalidParquet => localizer.Format("Error_ImportInvalidParquet", name),
            ImportFileError.ParquetTooLarge => localizer.Format("Error_ImportParquetTooLarge", name),
            _ => localizer.Format("Error_ImportFileUnreadable", name, exception.InnerException?.Message),
        };
    }
}
