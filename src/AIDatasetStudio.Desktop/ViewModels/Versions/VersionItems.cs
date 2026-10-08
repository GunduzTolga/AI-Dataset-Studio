using System.Globalization;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Desktop.ViewModels.Versions;

/// <summary>
/// The dataset whose versions are shown.
/// </summary>
/// <param name="RecordCount">Records in the working copy, for the "new version" summary.</param>
public sealed record VersionsTarget(Guid DatasetId, string Name, DatasetType Type, long RecordCount);

/// <summary>
/// The working copy (the dataset as it is now) or one frozen version, in the list of versions.
/// </summary>
public sealed class VersionItemViewModel
{
    private VersionItemViewModel(VersionInfo? version, string title, string subtitle, string? counts)
    {
        Version = version;
        Title = title;
        Subtitle = subtitle;
        CountsText = counts;
    }

    /// <summary>
    /// <c>null</c> for the working copy.
    /// </summary>
    public VersionInfo? Version { get; }

    public Guid? VersionId => Version?.Id;

    public bool IsWorkingCopy => Version is null;

    public bool IsVersion => Version is not null;

    /// <summary>
    /// "v1.2.0" or "Working copy".
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// When the version was made; what the working copy is.
    /// </summary>
    public string Subtitle { get; }

    /// <summary>
    /// "1,234 records · 980 approved" for a version.
    /// </summary>
    public string? CountsText { get; }

    public bool HasCounts => CountsText is not null;

    public string? Description => Version?.Description;

    public bool HasDescription => Description is not null;

    public static VersionItemViewModel WorkingCopy(Localizer localizer) =>
        new(null, localizer["Versions_WorkingCopy"], localizer["Versions_WorkingCopySubtitle"], null);

    public static VersionItemViewModel For(Localizer localizer, VersionInfo version) =>
        new(
            version,
            VersionText.Tag(version.Number),
            VersionText.Date(version.CreatedAt),
            localizer.Format("Versions_VersionCounts", version.RecordCount, version.ApprovedCount));

    public override string ToString() => Title;
}

/// <summary>
/// A chip that narrows the differences to one kind, with how many there are.
/// </summary>
/// <param name="Kind">The kind, or <c>null</c> for all.</param>
public sealed record KindFilterOption(DifferenceKind? Kind, string Label);

/// <summary>
/// A record that differs, in the list of differences.
/// </summary>
public sealed record ChangeItemViewModel(RecordDifference Difference, string KindText, string Preview, string? StatusText)
{
    public DifferenceKind Kind => Difference.Kind;

    public string OrdinalText => "#" + Difference.Ordinal.ToString(CultureInfo.CurrentCulture);

    public bool HasStatusText => StatusText is not null;

    public bool IsAdded => Kind == DifferenceKind.Added;

    public bool IsRemoved => Kind == DifferenceKind.Removed;

    public bool IsModified => Kind == DifferenceKind.Modified;

    public bool IsStatusChanged => Kind == DifferenceKind.StatusChanged;
}

/// <summary>
/// One field of the selected record in the older and the newer state.
/// </summary>
/// <param name="Label">"Output", "3 · Assistant"…</param>
/// <param name="Badge">"Changed", "Added" or "Removed"; <c>null</c> when the field is the same.</param>
/// <param name="OldSegments">The older text with deletions marked; empty when the field is only in the newer state.</param>
/// <param name="NewSegments">The newer text with insertions marked; empty when the field is only in the older state.</param>
public sealed record FieldDiffViewModel(
    string Label,
    string? Badge,
    IReadOnlyList<DiffSegment> OldSegments,
    IReadOnlyList<DiffSegment> NewSegments,
    bool HasOld,
    bool HasNew)
{
    public bool IsChanged => Badge is not null;

    /// <summary>
    /// Both texts side by side; otherwise the one text there is.
    /// </summary>
    public bool ShowBoth => IsChanged && HasOld && HasNew;

    public bool ShowSingle => !ShowBoth;

    /// <summary>
    /// The text shown alone: the newer one, or the older one when the field was removed.
    /// </summary>
    public IReadOnlyList<DiffSegment> SingleSegments => HasNew ? NewSegments : OldSegments;

    public bool HasBadge => Badge is not null;
}

internal static class VersionText
{
    public static string Tag(string number) => "v" + number;

    public static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Kind(Localizer localizer, DifferenceKind kind) => localizer[$"Difference_{kind}"];

    /// <summary>
    /// The label of a record field: "Instruction", "System prompt", "3 · Assistant"…
    /// </summary>
    public static string Field(Localizer localizer, RecordField field) => field.Kind == RecordFieldKind.Message
        ? $"{field.Turn.ToString(CultureInfo.CurrentCulture)} · {localizer[$"Field_{field.Role}"]}"
        : localizer[$"Field_{field.Kind}"];

    /// <summary>
    /// A field without text on one side is still shown, as "(empty)", so changes to and from empty read clearly.
    /// </summary>
    public static IReadOnlyList<DiffSegment> OrEmpty(Localizer localizer, IReadOnlyList<DiffSegment> segments) =>
        segments.Count == 0 ? [new DiffSegment(DiffKind.Equal, localizer["Field_Empty"])] : segments;
}
