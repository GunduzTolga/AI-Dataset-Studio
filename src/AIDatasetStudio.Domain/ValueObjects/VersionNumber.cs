using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AIDatasetStudio.Domain.Common;

namespace AIDatasetStudio.Domain.ValueObjects;

/// <summary>
/// A dataset version in semantic versioning form, "MAJOR.MINOR.PATCH" (SDD §14.1). Shown with a
/// leading "v" (v1.2.0) but stored without it.
/// </summary>
public readonly record struct VersionNumber(int Major, int Minor, int Patch) : IComparable<VersionNumber>
{
    /// <summary>
    /// The largest value of one part; keeps numbers readable and far from overflow.
    /// </summary>
    public const int MaxPart = 999_999;

    public static VersionNumber First { get; } = new(1, 0, 0);

    /// <summary>
    /// Reads "1.2.3"; a leading "v" and surrounding spaces are allowed.
    /// </summary>
    /// <exception cref="DomainException">The text is not a version number.</exception>
    public static VersionNumber Parse(string? text) =>
        TryParse(text, out var number)
            ? number
            : throw new DomainException($"'{text}' is not a version number like 1.2.0.");

    public static bool TryParse([NotNullWhen(true)] string? text, out VersionNumber number)
    {
        number = default;
        if (text is null)
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] is 'v' or 'V')
        {
            span = span[1..];
        }

        Span<int> parts = stackalloc int[3];
        for (var i = 0; i < 3; i++)
        {
            var end = span.IndexOf('.');
            var part = i < 2 ? (end < 0 ? [] : span[..end]) : span;
            if (!TryParsePart(part, out parts[i]))
            {
                return false;
            }

            span = i < 2 ? span[(end + 1)..] : [];
        }

        number = new VersionNumber(parts[0], parts[1], parts[2]);
        return true;
    }

    public VersionNumber NextMajor() => new(Major + 1, 0, 0);

    public VersionNumber NextMinor() => new(Major, Minor + 1, 0);

    public VersionNumber NextPatch() => new(Major, Minor, Patch + 1);

    public int CompareTo(VersionNumber other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(VersionNumber left, VersionNumber right) => left.CompareTo(right) < 0;

    public static bool operator >(VersionNumber left, VersionNumber right) => left.CompareTo(right) > 0;

    public static bool operator <=(VersionNumber left, VersionNumber right) => left.CompareTo(right) <= 0;

    public static bool operator >=(VersionNumber left, VersionNumber right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    // Digits only, no sign, no leading zeros (semver), at most MaxPart.
    private static bool TryParsePart(ReadOnlySpan<char> part, out int value)
    {
        value = 0;
        if (part.Length is 0 or > 6 || (part.Length > 1 && part[0] == '0'))
        {
            return false;
        }

        foreach (var c in part)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return value <= MaxPart;
    }
}
