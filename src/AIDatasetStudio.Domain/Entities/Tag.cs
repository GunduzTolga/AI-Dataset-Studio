using System.Text.RegularExpressions;
using AIDatasetStudio.Domain.Common;

namespace AIDatasetStudio.Domain.Entities;

/// <summary>
/// A label for records, shared by all datasets of the workspace (SDD §5.1, §18). Categories are
/// tags with a group prefix: "kategori:hukuk" belongs to the group "kategori" (see <see cref="GroupOf"/>).
/// </summary>
public sealed partial class Tag
{
    public const int MaxNameLength = 100;
    public const string DefaultColorHex = "#808080";

    /// <summary>
    /// Tags with a shortcut are toggled with Mod+1 … Mod+<see cref="MaxShortcut"/> (SDD §6.2).
    /// </summary>
    public const int MaxShortcut = 5;

    private Tag()
    {
        Name = string.Empty;
        NameKey = string.Empty;
        ColorHex = DefaultColorHex;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// The name folded for case-insensitive uniqueness that also covers Turkish letters
    /// ("Şifre", "şifre" and "ŞİFRE" are one tag); see <see cref="FoldName"/>.
    /// </summary>
    public string NameKey { get; private set; }

    /// <summary>
    /// Display color in "#RRGGBB" form.
    /// </summary>
    public string ColorHex { get; private set; }

    /// <summary>
    /// 1 … <see cref="MaxShortcut"/>: the tag is toggled with Mod+<c>Shortcut</c>; unique in the workspace.
    /// </summary>
    public int? Shortcut { get; private set; }

    /// <summary>
    /// Colors offered for tags; they read on light and dark backgrounds alike.
    /// </summary>
    public static IReadOnlyList<string> Palette { get; } =
        ["#6366F1", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#EC4899", "#8B5CF6", "#64748B"];

    public static Tag Create(string name, DateTimeOffset now, string colorHex = DefaultColorHex)
    {
        var tag = new Tag { Id = Guid.CreateVersion7(now), ColorHex = ValidateColor(colorHex) };
        tag.Rename(name);
        return tag;
    }

    public void Rename(string name)
    {
        Name = Guard.NotBlank(name, nameof(name), MaxNameLength);
        NameKey = FoldName(Name);
    }

    /// <summary>
    /// Lowercase with I, İ and ı all folded to i, so that both Turkish and English casing rules
    /// match ("TITLE" / "title" / "tıtle"). SQLite's NOCASE only folds ASCII letters.
    /// </summary>
    public static string FoldName(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is 'I' or 'İ' or 'ı' ? 'i' : char.ToLowerInvariant(source[i]);
            }
        });

    public void Recolor(string colorHex) => ColorHex = ValidateColor(colorHex);

    /// <param name="shortcut">1 … <see cref="MaxShortcut"/>, or <c>null</c> for none.</param>
    public void AssignShortcut(int? shortcut)
    {
        if (shortcut is < 1 or > MaxShortcut)
        {
            throw new DomainException($"A tag shortcut must be between 1 and {MaxShortcut}.");
        }

        Shortcut = shortcut;
    }

    /// <summary>
    /// The group of a category tag: the part before the first ':' ("kategori:hukuk" → "kategori");
    /// <c>null</c> when the name has no group.
    /// </summary>
    public static string? GroupOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && name[..colon].Trim() is { Length: > 0 } group ? group : null;
    }

    private static string ValidateColor(string colorHex) =>
        ColorPattern().IsMatch(colorHex)
            ? colorHex.ToUpperInvariant()
            : throw new DomainException($"'{colorHex}' is not a #RRGGBB color.");

    // \z, not $: "$" also matches before a trailing newline.
    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z")]
    private static partial Regex ColorPattern();
}
