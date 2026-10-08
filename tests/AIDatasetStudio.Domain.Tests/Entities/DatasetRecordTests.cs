using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Domain.Tests.Entities;

public sealed class DatasetRecordTests
{
    private static readonly DateTimeOffset Later = TestData.Now.AddMinutes(5);

    [Fact]
    public void New_record_starts_as_draft_with_hash()
    {
        var dataset = TestData.Dataset();
        var payload = new InstructionPayload("i", "in", "out");

        var record = DatasetRecord.Create(dataset, payload, RecordLineage.Manual("ayse"), ordinal: 7, TestData.Now);

        Assert.Equal(RecordStatus.Draft, record.Status);
        Assert.Equal(dataset.Id, record.DatasetId);
        Assert.Equal(DatasetType.Instruction, record.Type);
        Assert.Equal(7, record.Ordinal);
        Assert.Equal(payload.ComputeContentHash(), record.ContentHash);
        Assert.Equal(TestData.Now, record.CreatedAt);
        Assert.Equal(TestData.Now, record.UpdatedAt);
        Assert.Equal("ayse", record.Lineage.CreatedBy);
    }

    [Fact]
    public void Payload_type_must_match_dataset_type()
    {
        var dataset = TestData.Dataset(DatasetType.Chat);

        Assert.Throws<DomainException>(() =>
            DatasetRecord.Create(dataset, new InstructionPayload("i", "", "o"), RecordLineage.Manual(), 0, TestData.Now));
    }

    [Fact]
    public void Editing_content_moves_record_to_review()
    {
        var record = CreateRecord();
        record.ChangeStatus(RecordStatus.Approved, TestData.Now);

        var changed = record.UpdatePayload(new InstructionPayload("i", "in", "better out"), Later);

        Assert.True(changed);
        Assert.Equal(RecordStatus.InReview, record.Status);
        Assert.Equal(Later, record.UpdatedAt);
        Assert.Equal(record.Payload.ComputeContentHash(), record.ContentHash);
    }

    [Fact]
    public void Saving_identical_content_is_a_no_op()
    {
        var record = CreateRecord();
        record.ChangeStatus(RecordStatus.Approved, TestData.Now);

        var changed = record.UpdatePayload(new InstructionPayload("i", "in", "out"), Later);

        Assert.False(changed);
        Assert.Equal(RecordStatus.Approved, record.Status);
        Assert.Equal(TestData.Now, record.UpdatedAt);
    }

    [Fact]
    public void Archived_records_cannot_be_edited()
    {
        var record = CreateRecord();
        record.ChangeStatus(RecordStatus.Archived, TestData.Now);

        Assert.Throws<DomainException>(() => record.UpdatePayload(new InstructionPayload("x", "", "y"), Later));
    }

    [Fact]
    public void Invalid_status_change_throws()
    {
        var record = CreateRecord();

        Assert.Throws<DomainException>(() => record.ChangeStatus(RecordStatus.ExportReady, Later));
        Assert.Equal(RecordStatus.Draft, record.Status);
    }

    [Fact]
    public void Tags_are_added_once_and_can_be_removed()
    {
        var record = CreateRecord();
        var tag = Tag.Create("finance", TestData.Now, "#ff8800");

        Assert.True(record.AddTag(tag, Later));
        Assert.False(record.AddTag(tag, Later));
        Assert.Single(record.Tags);

        Assert.True(record.RemoveTag(tag.Id, Later));
        Assert.Empty(record.Tags);
    }

    private static DatasetRecord CreateRecord() =>
        DatasetRecord.Create(TestData.Dataset(), new InstructionPayload("i", "in", "out"), RecordLineage.Manual(), 0, TestData.Now);
}
