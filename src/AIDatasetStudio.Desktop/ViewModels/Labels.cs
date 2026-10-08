using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// Localized display names for domain enums. Resource keys are "{EnumName}_{Member}".
/// </summary>
internal static class Labels
{
    /// <summary>
    /// Dataset types that can be created in this version (their payload types exist).
    /// </summary>
    public static readonly IReadOnlyList<DatasetType> CreatableDatasetTypes =
        [DatasetType.Instruction, DatasetType.Chat, DatasetType.Preference];

    public static string For(Localizer localizer, DatasetType type) => localizer[$"DatasetType_{type}"];

    public static string For(Localizer localizer, RecordStatus status) => localizer[$"RecordStatus_{status}"];

    public static string For(Localizer localizer, RecordSourceKind source) => localizer[$"Source_{source}"];
}
