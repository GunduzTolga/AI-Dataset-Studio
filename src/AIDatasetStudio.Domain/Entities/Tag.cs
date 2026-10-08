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
        ColorHex = DefaultColorHex;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// Display color in "#RRGGBB" form.
    /// </summary>
    public string ColorHex { get; private set; }

    public static Tag Create(string name, DateTimeOffset now, string colorHex = DefaultColorHex) => new()
    {
        Id = Guid.CreateVersion7(now),
        Name = Guard.NotBlank(name, nameof(name), MaxNameLength),
        ColorHex = ValidateColor(colorHex),
    };

    public void Rename(string name) => Name = Guard.NotBlank(name, nameof(name), MaxNameLength);

    public void Recolor(string colorHex) => ColorHex = ValidateColor(colorHex);

    private static string ValidateColor(string colorHex) =>
        ColorPattern().IsMatch(colorHex)
            ? colorHex.ToUpperInvariant()
            : throw new DomainException($"'{colorHex}' is not a #RRGGBB color.");

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}
