using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Tests.Enums;

public sealed class RecordStatusTransitionsTests
{
    [Theory]
    [InlineData(RecordStatus.Draft, RecordStatus.InReview)]
    [InlineData(RecordStatus.InReview, RecordStatus.Approved)]
    [InlineData(RecordStatus.Draft, RecordStatus.Approved)] // Mod+Enter during speed review
    [InlineData(RecordStatus.Approved, RecordStatus.InReview)] // edited after approval
    [InlineData(RecordStatus.Approved, RecordStatus.Archived)]
    [InlineData(RecordStatus.Archived, RecordStatus.Draft)] // restore
    public void Allows_expected_transitions(RecordStatus from, RecordStatus to)
    {
        Assert.True(RecordStatusTransitions.CanTransition(from, to));
    }

    [Theory]
    [InlineData(RecordStatus.Archived, RecordStatus.Approved)]
    [InlineData(RecordStatus.Archived, RecordStatus.InReview)]
    [InlineData(RecordStatus.Approved, RecordStatus.Draft)]
    public void Rejects_unexpected_transitions(RecordStatus from, RecordStatus to)
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
