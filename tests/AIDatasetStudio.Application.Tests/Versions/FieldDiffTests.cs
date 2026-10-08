using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Versions;

public sealed class FieldDiffTests
{
    [Fact]
    public void Instruction_fields_pair_up_by_name()
    {
        var fields = FieldDiff.Compare(
            new InstructionPayload("Soru", "", "Eski yanıt"),
            new InstructionPayload("Soru", "", "Yeni yanıt"));

        Assert.Equal([RecordFieldKind.Instruction, RecordFieldKind.Input, RecordFieldKind.Output], fields.Select(f => f.Kind));
        Assert.Equal([false, false, true], fields.Select(f => f.IsChanged));
        Assert.Equal("Yeni", fields[2].Diff.Segments.Single(s => s.Kind == DiffKind.Inserted).Text);
    }

    [Fact]
    public void Preference_fields_pair_up_by_name()
    {
        var fields = FieldDiff.Compare(new PreferencePayload("İstem", "İyi", "Kötü"), new PreferencePayload("İstem", "Daha iyi", "Kötü"));

        Assert.Equal([RecordFieldKind.Prompt, RecordFieldKind.Chosen, RecordFieldKind.Rejected], fields.Select(f => f.Kind));
        Assert.Equal([false, true, false], fields.Select(f => f.IsChanged));
    }

    [Fact]
    public void An_inserted_turn_leaves_later_turns_unchanged()
    {
        var before = Chat(null, (ChatRole.User, "Merhaba"), (ChatRole.Assistant, "Selam"), (ChatRole.User, "Nasılsın?"), (ChatRole.Assistant, "İyiyim"));
        var after = Chat(null, (ChatRole.User, "Merhaba"), (ChatRole.Assistant, "Selam"), (ChatRole.User, "Adın ne?"), (ChatRole.Assistant, "Asistan"),
            (ChatRole.User, "Nasılsın?"), (ChatRole.Assistant, "İyiyim"));

        var fields = FieldDiff.Compare(before, after);

        Assert.Equal(6, fields.Count);
        Assert.Equal([false, false, true, true, false, false], fields.Select(f => f.IsChanged));
        Assert.Null(fields[2].Old);
        Assert.Equal((3, 5), (fields[4].Old!.Turn, fields[4].New!.Turn));
    }

    [Fact]
    public void Edited_turns_pair_up_and_removed_turns_stand_alone()
    {
        var before = Chat("Kısa yanıt ver.", (ChatRole.User, "Soru"), (ChatRole.Assistant, "Uzun yanıt"), (ChatRole.User, "Teşekkürler"));
        var after = Chat(null, (ChatRole.User, "Soru"), (ChatRole.Assistant, "Kısa yanıt"));

        var fields = FieldDiff.Compare(before, after);

        Assert.Equal(RecordFieldKind.SystemPrompt, fields[0].Kind);
        Assert.Null(fields[0].New);
        Assert.False(fields[1].IsChanged);
        Assert.True(fields[2].IsChanged);
        Assert.NotNull(fields[2].Old);
        Assert.NotNull(fields[2].New);
        Assert.Equal("Teşekkürler", fields[3].Old!.Text);
        Assert.Null(fields[3].New);
    }

    [Fact]
    public void A_turn_that_changed_speaker_is_changed()
    {
        var fields = FieldDiff.Compare(Chat(null, (ChatRole.User, "Aynı")), Chat(null, (ChatRole.Assistant, "Aynı")));

        Assert.True(Assert.Single(fields).IsChanged);
    }

    [Fact]
    public void A_record_on_one_side_only_lists_its_fields()
    {
        var added = FieldDiff.Compare(null, new InstructionPayload("Soru", "", "Yanıt"));
        var removed = FieldDiff.Compare(new InstructionPayload("Soru", "", "Yanıt"), null);

        Assert.Equal(3, added.Count);
        Assert.All(added, f => Assert.Null(f.Old));
        Assert.All(removed, f => Assert.Null(f.New));
        Assert.All(added.Concat(removed), f => Assert.True(f.IsChanged));
    }

    private static ChatPayload Chat(string? system, params (ChatRole Role, string Text)[] messages) =>
        new(system, [.. messages.Select(m => new ChatMessage(m.Role, m.Text))]);
}
