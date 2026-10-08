using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Tests.Enums;

public sealed class RecordStatusTransitionsTests
{
    [Theory]
    [InlineData(RecordStatus.Draft, RecordStatus.InReview)]
    [InlineData(RecordStatus.InReview, RecordStatus.Approved)]
    [InlineData(RecordStatus.Approved, RecordStatus.ExportReady)]
    [InlineData(RecordStatus.Draft, RecordStatus.Approved)] // Ctrl+Enter during speed review
    [InlineData(RecordStatus.ExportReady, RecordStatus.Archived)]
    [InlineData(RecordStatus.Archived, RecordStatus.Draft)] // restore
    public void Allows_expected_transitions(RecordStatus from, RecordStatus to)
    {
        Assert.True(RecordStatusTransitions.CanTransition(from, to));
    }

    [Theory]
    [InlineData(RecordStatus.Draft, RecordStatus.ExportReady)]
    [InlineData(RecordStatus.InReview, RecordStatus.ExportReady)]
    [InlineData(RecordStatus.Archived, RecordStatus.Approved)]
    public void Rejects_skipping_quality_gates(RecordStatus from, RecordStatus to)
    {
        Assert.False(RecordStatusTransitions.CanTransition(from, to));
    }

    [Fact]
    public void Every_status_can_be_archived_or_is_archived()
    {
        foreach (var status in Enum.GetValues<RecordStatus>())
        {
            Assert.True(RecordStatusTransitions.CanTransition(status, RecordStatus.Archived));
        }
    }
}
