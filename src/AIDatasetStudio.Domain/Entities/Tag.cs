using System.Text.RegularExpressions;
using AIDatasetStudio.Domain.Common;

namespace AIDatasetStudio.Domain.Entities;

public sealed partial class Tag
{
    public const int MaxNameLength = 100;
    public const string DefaultColorHex = "#808080";

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

    private static string ValidateColor(string colorHex) =>
        ColorPattern().IsMatch(colorHex)
            ? colorHex.ToUpperInvariant()
            : throw new DomainException($"'{colorHex}' is not a #RRGGBB color.");

    // \z, not $: "$" also matches before a trailing newline.
    [GeneratedRegex(@"\A#[0-9a-fA-F]{6}\z")]
    private static partial Regex ColorPattern();
}
