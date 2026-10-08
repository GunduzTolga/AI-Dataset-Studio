using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AIDatasetStudio.Infrastructure.Persistence.Converters;

/// <summary>
/// Stores timestamps as UTC Unix milliseconds. SQLite has no native date type and EF Core cannot
/// order or compare <see cref="DateTimeOffset"/> stored as text, so an integer keeps queries
/// sortable and the file readable from other tools.
/// </summary>
internal sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.ToUnixTimeMilliseconds(),
    value => DateTimeOffset.FromUnixTimeMilliseconds(value));
