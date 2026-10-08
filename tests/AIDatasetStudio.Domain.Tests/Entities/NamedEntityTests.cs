using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Domain.Tests.Entities;

public sealed class NamedEntityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_names_are_rejected(string name)
    {
        Assert.Throws<DomainException>(() => Project.Create(name, TestData.Now));
        Assert.Throws<DomainException>(() => Workspace.Create(name, TestData.Now));
        Assert.Throws<DomainException>(() => Tag.Create(name, TestData.Now));
    }

    [Fact]
    public void Names_are_trimmed()
    {
        Assert.Equal("Support Bot", Project.Create("  Support Bot ", TestData.Now).Name);
    }

    [Fact]
    public void Overlong_names_are_rejected()
    {
        Assert.Throws<DomainException>(() => Project.Create(new string('x', Project.MaxNameLength + 1), TestData.Now));
    }

    [Fact]
    public void Dataset_requires_a_project_and_a_known_type()
    {
        Assert.Throws<DomainException>(() => Dataset.Create(Guid.Empty, "d", DatasetType.Chat, TestData.Now));
        Assert.Throws<DomainException>(() => Dataset.Create(Guid.CreateVersion7(), "d", (DatasetType)999, TestData.Now));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Tag_color_must_be_hex(string color)
    {
        Assert.Throws<DomainException>(() => Tag.Create("t", TestData.Now, color));
    }

    [Fact]
    public void Tag_color_is_normalized_to_uppercase()
    {
        Assert.Equal("#AABBCC", Tag.Create("t", TestData.Now, "#aabbcc").ColorHex);
    }

    [Fact]
    public void Ids_are_time_ordered_version_7_guids()
    {
        var project = Project.Create("p", TestData.Now);

        Assert.Equal(7, project.Id.Version);
    }
}
