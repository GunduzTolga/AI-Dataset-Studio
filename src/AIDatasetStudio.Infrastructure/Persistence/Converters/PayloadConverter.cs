using AIDatasetStudio.Domain.Payloads;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AIDatasetStudio.Infrastructure.Persistence.Converters;

internal sealed class PayloadConverter() : ValueConverter<RecordPayload, string>(
    payload => PayloadJson.Serialize(payload),
    json => PayloadJson.Deserialize(json));

/// <summary>
/// Payloads are immutable records with value equality, so the instance itself is a valid snapshot.
/// </summary>
internal sealed class PayloadComparer() : ValueComparer<RecordPayload>(
    (left, right) => object.Equals(left, right),
    payload => payload.GetHashCode(),
    payload => payload);
