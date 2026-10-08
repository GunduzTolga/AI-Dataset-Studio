using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    public static Dataset Dataset(DatasetType type = DatasetType.Instruction) =>
        Domain.Entities.Dataset.Create(Guid.CreateVersion7(), "FAQ", type, Now);
}
