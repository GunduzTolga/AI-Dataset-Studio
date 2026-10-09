using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Domain.Tests.ValueObjects;

public sealed class VersionNumberTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("v2.10.3", 2, 10, 3)]
    [InlineData(" V0.1.0 ", 0, 1, 0)]
    [InlineData("999999.0.0", 999_999, 0, 0)]
    public void Valid_numbers_are_read(string text, int major, int minor, int patch)
    {
        Assert.Equal(new VersionNumber(major, minor, patch), VersionNumber.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1..0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.x")]
    [InlineData("vv1.0.0")]
    [InlineData("1000000.0.0")]
    [InlineData("١.٠.٠")] // Arabic-Indic digits are not ASCII digits
    public void Invalid_numbers_are_rejected(string? text)
    {
        Assert.False(VersionNumber.TryParse(text, out _));
        Assert.Throws<DomainException>(() => VersionNumber.Parse(text));
    }

    [Fact]
    public void Numbers_are_written_without_the_v()
    {
        Assert.Equal("1.2.3", VersionNumber.Parse("v1.2.3").ToString());
    }

    [Fact]
    public void Next_numbers_follow_semantic_versioning()
    {
        var number = new VersionNumber(1, 4, 2);

        Assert.Equal(new VersionNumber(2, 0, 0), number.NextMajor());
        Assert.Equal(new VersionNumber(1, 5, 0), number.NextMinor());
        Assert.Equal(new VersionNumber(1, 4, 3), number.NextPatch());
    }

    [Fact]
    public void Numbers_compare_part_by_part_not_as_text()
    {
        var sorted = new[] { "1.10.0", "1.9.0", "0.9.9", "2.0.0", "1.9.1" }.Select(VersionNumber.Parse).Order().Select(n => n.ToString());

        Assert.Equal(["0.9.9", "1.9.0", "1.9.1", "1.10.0", "2.0.0"], sorted);
        Assert.True(VersionNumber.Parse("1.10.0") > VersionNumber.Parse("1.9.0"));
    }

    [Fact]
    public void A_version_freezes_the_datasets_token_settings()
    {
        var dataset = TestData.Dataset();
        dataset.ChangeTokenSettings("cl100k_base", 8_192);

        var version = DatasetVersion.Create(dataset, VersionNumber.First, "  First run  ", 10, 7, TestData.Now);

        Assert.Equal("1.0.0", version.Number);
        Assert.Equal("First run", version.Description);
        Assert.Equal("cl100k_base", version.Tokenizer);
        Assert.Equal(8_192, version.ContextLength);
        Assert.Equal((10, 7), (version.RecordCount, version.ApprovedCount));
        Assert.Null(DatasetVersion.Create(dataset, VersionNumber.First, "  ", 0, 0, TestData.Now).Description);
        Assert.Throws<DomainException>(() =>
            DatasetVersion.Create(dataset, VersionNumber.First, new string('x', DatasetVersion.MaxDescriptionLength + 1), 0, 0, TestData.Now));
    }
}
