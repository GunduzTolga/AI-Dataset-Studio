using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Import;

public sealed class ImportConverterTests
{
    private static readonly Dictionary<TargetField, string> NoMapping = [];

    [Fact]
    public void Alpaca_rows_become_instruction_records()
    {
        var row = TestRows.Json("""{"instruction":"Özetle","input":"Metin","output":"Özet"}""");

        var result = Convert(row, SourceLayout.Alpaca, DatasetType.Instruction, Map(
            (TargetField.Instruction, "instruction"), (TargetField.Input, "input"), (TargetField.Output, "output")));

        Assert.Equal(new InstructionPayload("Özetle", "Metin", "Özet"), result.Payload);
    }

    [Fact]
    public void Csv_columns_become_chat_records()
    {
        var row = TestRows.Columns(("sistem", "Kısa yanıt ver."), ("soru", "İzin?"), ("cevap", "14 gün."));

        var result = Convert(row, SourceLayout.Custom, DatasetType.Chat, Map(
            (TargetField.SystemPrompt, "sistem"), (TargetField.User, "soru"), (TargetField.Assistant, "cevap")));

        Assert.Equal(new ChatPayload("Kısa yanıt ver.", [new(ChatRole.User, "İzin?"), new(ChatRole.Assistant, "14 gün.")]), result.Payload);
    }

    [Fact]
    public void Rows_with_only_empty_values_are_skipped()
    {
        var row = TestRows.Columns(("a", " "), ("b", ""));

        var result = Convert(row, SourceLayout.Custom, DatasetType.Instruction, Map((TargetField.Instruction, "a"), (TargetField.Output, "b")));

        Assert.Equal(ImportErrorCode.EmptyRecord, result.Error?.Code);
    }

    [Fact]
    public void Missing_json_keys_read_as_empty()
    {
        var row = TestRows.Json("""{"instruction":"Soru"}""");

        var result = Convert(row, SourceLayout.Alpaca, DatasetType.Instruction, Map((TargetField.Instruction, "instruction"), (TargetField.Output, "output")));

        Assert.Equal(new InstructionPayload("Soru", "", ""), result.Payload);
    }

    [Fact]
    public void Non_text_values_keep_their_json_text()
    {
        var row = TestRows.Json("""{"q":"Kaç?","a":42}""");

        var result = Convert(row, SourceLayout.Custom, DatasetType.Instruction, Map((TargetField.Instruction, "q"), (TargetField.Output, "a")));

        Assert.Equal("42", ((InstructionPayload)result.Payload!).Output);
    }

    [Fact]
    public void Openai_messages_become_chat_records_with_the_system_prompt_split_out()
    {
        var row = TestRows.Json("""
            {"messages":[
              {"role":"system","content":"Be brief."},
              {"role":"user","content":"Hi"},
              {"role":"assistant","content":[{"type":"text","text":"Hello"},{"type":"text","text":"!"}]},
              {"role":"user","content":"Bye"}
            ]}
            """);

        var result = Convert(row, SourceLayout.OpenAiMessages, DatasetType.Chat, NoMapping);

        var chat = Assert.IsType<ChatPayload>(result.Payload);
        Assert.Equal("Be brief.", chat.SystemPrompt);
        Assert.Equal(["User:Hi", "Assistant:Hello!", "User:Bye"], chat.Messages.Select(m => $"{m.Role}:{m.Content}"));
    }

    [Fact]
    public void Sharegpt_roles_are_understood()
    {
        var row = TestRows.Json("""{"conversations":[{"from":"human","value":"Soru"},{"from":"gpt","value":"Cevap"}]}""");

        var result = Convert(row, SourceLayout.ShareGpt, DatasetType.Chat, NoMapping);

        Assert.Equal(new ChatPayload(null, [new(ChatRole.User, "Soru"), new(ChatRole.Assistant, "Cevap")]), result.Payload);
    }

    [Fact]
    public void Single_turn_conversations_can_become_instruction_records()
    {
        var row = TestRows.Json("""{"conversations":[{"from":"human","value":"Soru"},{"from":"gpt","value":"Cevap"}]}""");

        var result = Convert(row, SourceLayout.ShareGpt, DatasetType.Instruction, NoMapping);

        Assert.Equal(new InstructionPayload("Soru", "", "Cevap"), result.Payload);
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":"a"},{"role":"assistant","content":"b"},{"role":"user","content":"c"}]}""", ImportErrorCode.MultiTurnNotSupported)]
    [InlineData("""{"messages":[{"role":"system","content":"s"},{"role":"user","content":"a"},{"role":"assistant","content":"b"}]}""", ImportErrorCode.SystemPromptNotSupported)]
    [InlineData("""{"messages":[{"role":"user","content":"a"},{"role":"tool","content":"{}"}]}""", ImportErrorCode.UnknownRole)]
    [InlineData("""{"messages":"not a list"}""", ImportErrorCode.NoConversation)]
    [InlineData("""{"messages":[1,2]}""", ImportErrorCode.NoConversation)]
    [InlineData("""{"messages":[{"role":"system","content":"only system"}]}""", ImportErrorCode.NoConversation)]
    public void Conversations_that_do_not_fit_an_instruction_record_are_explained(string json, ImportErrorCode expected)
    {
        var result = Convert(TestRows.Json(json), SourceLayout.OpenAiMessages, DatasetType.Instruction, NoMapping);

        Assert.Null(result.Payload);
        Assert.Equal(expected, result.Error?.Code);
    }

    [Fact]
    public void Unknown_roles_are_named_in_the_error()
    {
        var row = TestRows.Json("""{"messages":[{"role":"user","content":"a"},{"role":"tool","content":"{}"}]}""");

        var result = Convert(row, SourceLayout.OpenAiMessages, DatasetType.Chat, NoMapping);

        Assert.Equal(new ImportRowError(1, ImportErrorCode.UnknownRole, "tool"), result.Error);
    }

    [Fact]
    public void Hugging_face_dpo_rows_with_text_values_become_preference_records()
    {
        var row = TestRows.Json("""{"prompt":"p","chosen":"good","rejected":"bad"}""");

        var result = Convert(row, SourceLayout.HuggingFaceDpo, DatasetType.Preference, PreferenceMapping);

        Assert.Equal(new PreferencePayload("p", "good", "bad"), result.Payload);
    }

    [Fact]
    public void Hugging_face_dpo_rows_with_conversations_use_the_last_turns()
    {
        var row = TestRows.Json("""
            {"chosen":[{"role":"user","content":"Soru?"},{"role":"assistant","content":"İyi cevap"}],
             "rejected":[{"role":"user","content":"Soru?"},{"role":"assistant","content":"Kötü cevap"}]}
            """);

        var result = Convert(row, SourceLayout.HuggingFaceDpo, DatasetType.Preference, PreferenceMapping);

        Assert.Equal(new PreferencePayload("Soru?", "İyi cevap", "Kötü cevap"), result.Payload);
    }

    [Fact]
    public void Openai_preference_rows_become_preference_records()
    {
        var row = TestRows.Json("""
            {"input":{"messages":[{"role":"user","content":"Q"}]},
             "preferred_output":[{"role":"assistant","content":"good"}],
             "non_preferred_output":[{"role":"assistant","content":"bad"}]}
            """);

        var result = Convert(row, SourceLayout.OpenAiPreference, DatasetType.Preference, NoMapping);

        Assert.Equal(new PreferencePayload("Q", "good", "bad"), result.Payload);
    }

    [Fact]
    public void Openai_preference_rows_with_context_are_not_flattened_silently()
    {
        var row = TestRows.Json("""
            {"input":{"messages":[{"role":"user","content":"a"},{"role":"assistant","content":"b"},{"role":"user","content":"c"}]},
             "preferred_output":[{"role":"assistant","content":"good"}],
             "non_preferred_output":[{"role":"assistant","content":"bad"}]}
            """);

        var result = Convert(row, SourceLayout.OpenAiPreference, DatasetType.Preference, NoMapping);

        Assert.Equal(ImportErrorCode.MultiTurnNotSupported, result.Error?.Code);
    }

    [Fact]
    public void Layouts_that_do_not_fit_the_dataset_are_rejected()
    {
        var row = TestRows.Json("""{"messages":[{"role":"user","content":"a"}]}""");

        var result = Convert(row, SourceLayout.OpenAiMessages, DatasetType.Preference, NoMapping);

        Assert.Equal(ImportErrorCode.UnsupportedLayout, result.Error?.Code);
    }

    private static readonly Dictionary<TargetField, string> PreferenceMapping = Map(
        (TargetField.Prompt, "prompt"), (TargetField.Chosen, "chosen"), (TargetField.Rejected, "rejected"));

    private static Dictionary<TargetField, string> Map(params (TargetField Field, string Column)[] pairs) =>
        pairs.ToDictionary(p => p.Field, p => p.Column);

    private static ImportRowResult Convert(ISourceRow row, SourceLayout layout, DatasetType type, IReadOnlyDictionary<TargetField, string> mapping) =>
        ImportConverter.Convert(row, new ImportPlan("file.jsonl", ImportFormat.JsonLines, null, layout, mapping, type));
}
