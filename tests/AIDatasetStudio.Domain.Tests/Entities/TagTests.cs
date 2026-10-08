using AIDatasetStudio.Domain.Common;
using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Domain.Tests.Entities;

public sealed class TagTests
{
    [Theory]
    [InlineData("kategori:hukuk", "kategori")]
    [InlineData(" Kategori :teknik", "Kategori")]
    [InlineData("dil:tr:resmi", "dil")]
    [InlineData("hukuk", null)]
    [InlineData(":boş", null)]
    [InlineData("  :x", null)]
    public void The_group_is_the_part_before_the_first_colon(string name, string? group)
    {
        Assert.Equal(group, Tag.GroupOf(name));
    }

    [Fact]
    public void Shortcuts_are_one_to_five_or_none()
    {
        var tag = Tag.Create("hukuk", TestData.Now);

        tag.AssignShortcut(5);
        Assert.Equal(5, tag.Shortcut);
        tag.AssignShortcut(null);
        Assert.Null(tag.Shortcut);
        Assert.Throws<DomainException>(() => tag.AssignShortcut(0));
        Assert.Throws<DomainException>(() => tag.AssignShortcut(Tag.MaxShortcut + 1));
    }

    [Fact]
    public void The_palette_colors_are_valid()
    {
        Assert.All(Tag.Palette, color => Assert.Equal(color, Tag.Create("x", TestData.Now, color).ColorHex));
        Assert.Equal(Tag.Palette.Count, Tag.Palette.Distinct().Count());
    }

    [Fact]
    public void Setting_tags_replaces_them_and_reports_a_change()
    {
        var record = DatasetRecord.Create(TestData.Dataset(), new InstructionPayload("a", "", "b"), RecordLineage.Manual(), 1, TestData.Now);
        var hukuk = Tag.Create("hukuk", TestData.Now);
        var teknik = Tag.Create("teknik", TestData.Now);
        var later = TestData.Now.AddHours(1);

        Assert.True(record.SetTags([hukuk, teknik, hukuk], later));
        Assert.Equal([hukuk, teknik], record.Tags);
        Assert.Equal(later, record.UpdatedAt);
        Assert.False(record.SetTags([teknik, hukuk], later.AddHours(1)));
        Assert.Equal(later, record.UpdatedAt);
        Assert.True(record.SetTags([], later));
        Assert.Empty(record.Tags);
    }
}
