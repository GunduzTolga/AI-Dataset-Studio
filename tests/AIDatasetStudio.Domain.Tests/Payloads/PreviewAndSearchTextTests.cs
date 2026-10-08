using AIDatasetStudio.Domain.Entities;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using AIDatasetStudio.Domain.ValueObjects;

namespace AIDatasetStudio.Domain.Tests.Payloads;

public sealed class PreviewAndSearchTextTests
{
    [Fact]
    public void Instruction_preview_is_the_instruction_on_one_line()
    {
        var payload = new InstructionPayload("Summarize\nthe   text\tbelow.", "long input", "summary");

        Assert.Equal("Summarize the text below.", payload.GetPreviewText());
    }

    [Fact]
    public void Instruction_preview_falls_back_to_input_then_output()
    {
        Assert.Equal("only input", new InstructionPayload("  ", "only input", "out").GetPreviewText());
        Assert.Equal("only output", new InstructionPayload("", "", "only output").GetPreviewText());
    }

    [Fact]
    public void Chat_preview_is_the_first_user_turn()
    {
        var payload = new ChatPayload("You are helpful.", [
            new ChatMessage(ChatRole.Assistant, "Hello!"),
            new ChatMessage(ChatRole.User, "Yıllık izin hakkım ne zaman başlar?"),
        ]);

        Assert.Equal("Yıllık izin hakkım ne zaman başlar?", payload.GetPreviewText());
    }

    [Fact]
    public void Chat_preview_without_user_turns_uses_other_text()
    {
        Assert.Equal("Hello!", new ChatPayload("sys", [new ChatMessage(ChatRole.Assistant, "Hello!")]).GetPreviewText());
        Assert.Equal("sys", new ChatPayload("sys", []).GetPreviewText());
        Assert.Equal(string.Empty, new ChatPayload(null, []).GetPreviewText());
    }

    [Fact]
    public void Long_previews_are_cut_with_an_ellipsis()
    {
        var preview = new PreferencePayload(new string('a', 500), "c", "r").GetPreviewText(maxLength: 50);

        Assert.Equal(50, preview.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Cutting_never_splits_an_emoji()
    {
        // The cut at 199 UTF-16 units would fall between the two halves of the emoji.
        var preview = new InstructionPayload(new string('a', 198) + "😀x", "", "").GetPreviewText();

        Assert.Equal(new string('a', 198) + "…", preview);
        Assert.DoesNotContain(preview, char.IsSurrogate);
    }

    [Fact]
    public void Searchable_texts_cover_every_text_field()
    {
        Assert.Equal(["i", "in", "out"], new InstructionPayload("i", "in", "out").GetSearchableTexts());
        Assert.Equal(["p", "c", "r"], new PreferencePayload("p", "c", "r").GetSearchableTexts());
        Assert.Equal(
            ["sys", "q", "a"],
            new ChatPayload("sys", [new ChatMessage(ChatRole.User, "q"), new ChatMessage(ChatRole.Assistant, "a")]).GetSearchableTexts());
    }

    [Fact]
    public void Record_keeps_its_preview_in_sync_with_the_payload()
    {
        var dataset = TestData.Dataset();
        var record = DatasetRecord.Create(dataset, new InstructionPayload("first", "", ""), RecordLineage.Manual(), 0, TestData.Now);

        record.UpdatePayload(new InstructionPayload("second", "", ""), TestData.Now);

        Assert.Equal("second", record.Preview);
    }
}
