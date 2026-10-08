using System.Globalization;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Desktop.ViewModels.Export;

/// <summary>
/// The dataset being exported.
/// </summary>
public sealed record ExportTarget(Guid DatasetId, string Name, DatasetType Type);

/// <summary>
/// A target format with what it looks like and, when it cannot take every record of the
/// dataset, a warning (SDD §13.3).
/// </summary>
public sealed record FormatOption(ExportFormat Format, string Label, string Description, string? Warning)
{
    public bool HasWarning => Warning is not null;
}

public enum SplitMode
{
    None,
    Standard,
    TrainValidation,
    Custom,
}

public sealed record SplitModeOption(SplitMode Mode, string Label);

/// <summary>
/// A written file on the result screen.
/// </summary>
public sealed record ExportedFileItem(string Name, string Folder, string CountText);

/// <summary>
/// A reason records are left out, with how many; shown only when there are any.
/// </summary>
public sealed record SkipItem(string Text, string CountText, bool IsInfo = false);

/// <summary>
/// Localized names of formats and split parts.
/// </summary>
internal static class ExportText
{
    public static string Format(Localizer localizer, ExportFormat format) => localizer[$"ExportFormat_{format}"];

    /// <summary>
    /// What a line looks like; preference pairs have their own shape in OpenAI's format.
    /// </summary>
    public static string Description(Localizer localizer, ExportFormat format, DatasetType type) =>
        format == ExportFormat.OpenAi && type == DatasetType.Preference
            ? localizer["ExportFormat_OpenAiPreference_Hint"]
            : localizer[$"ExportFormat_{format}_Hint"];

    public static string Part(Localizer localizer, SplitPart part) => localizer[$"SplitPart_{part}"];

    public static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);
}
