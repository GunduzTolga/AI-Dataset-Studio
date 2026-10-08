using System.Globalization;

namespace AIDatasetStudio.Infrastructure.Persistence;

/// <summary>
/// Converts values to and from the exact representation EF Core uses in workspace files,
/// so raw SQL and EF Core read and write the same rows.
/// </summary>
internal static class SqliteValues
{
    /// <summary>
    /// EF Core's SQLite provider stores <see cref="System.Guid"/> as uppercase text.
    /// </summary>
    public static string Guid(Guid value) => value.ToString("D").ToUpperInvariant();

    public static Guid ReadGuid(string value) => System.Guid.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Matches <see cref="Converters.UnixMillisecondsConverter"/>.
    /// </summary>
    public static long Timestamp(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset ReadTimestamp(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public static object NullableText(string? value) => value is null ? DBNull.Value : value;
}
