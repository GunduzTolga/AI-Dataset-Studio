using System.Text.Json;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Tests.Import;

public sealed class ImportLayoutsTests
{
    [Theory]
    [InlineData("""{"instruction":"a","input":"","output":"b"}""", SourceLayout.Alpaca)]
    [InlineData("""{"prompt":"a","completion":"b"}""", SourceLayout.PromptCompletion)]
    [InlineData("""{"prompt":"a","chosen":"b","rejected":"c"}""", SourceLayout.HuggingFaceDpo)]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}]}""", SourceLayout.OpenAiMessages)]
    [InlineData("""{"conversations":[{"from":"human","value":"hi"}]}""", SourceLayout.ShareGpt)]
    [InlineData("""{"input":{"messages":[{"role":"user","content":"hi"}]},"preferred_output":[],"non_preferred_output":[]}""", SourceLayout.OpenAiPreference)]
    [InlineData("""{"soru":"a","cevap":"b"}""", SourceLayout.Custom)]
    public void Detects_the_layout_of_json_rows(string json, SourceLayout expected)
    {
        var sample = new[] { JsonDocument.Parse(json).RootElement };

        Assert.Equal(expected, ImportLayouts.DetectFromJson(sample));
    }

    [Fact]
    public void Detection_follows_the_majority_of_rows()
    {
        var sample = new[]
        {
            """{"messages":[]}""", """{"messages":[]}""", """{"instruction":"x","output":"y"}""",
        }.Select(j => JsonDocument.Parse(j).RootElement).ToList();

        Assert.Equal(SourceLayout.OpenAiMessages, ImportLayouts.DetectFromJson(sample));
    }

    [Theory]
    [InlineData(DatasetType.Instruction, new[] { "Soru", "Bağlam", "Cevap" }, new[] { "Instruction=Soru", "Input=Bağlam", "Output=Cevap" })]
    [InlineData(DatasetType.Instruction, new[] { "instruction", "input", "output" }, new[] { "Instruction=instruction", "Input=input", "Output=output" })]
    [InlineData(DatasetType.Chat, new[] { "System Prompt", "Kullanıcı", "Asistan" }, new[] { "SystemPrompt=System Prompt", "User=Kullanıcı", "Assistant=Asistan" })]
    [InlineData(DatasetType.Preference, new[] { "İSTEM", "Tercih Edilen", "Reddedilen" }, new[] { "Prompt=İSTEM", "Chosen=Tercih Edilen", "Rejected=Reddedilen" })]
    [InlineData(DatasetType.Instruction, new[] { "id", "notes" }, new string[0])]
    public void Suggests_mappings_from_english_and_turkish_names(DatasetType type, string[] columns, string[] expected)
    {
        var mapping = ImportLayouts.SuggestMapping(columns, type);

        Assert.Equal(expected.Order(StringComparer.Ordinal), mapping.Select(m => $"{m.Key}={m.Value}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_column_is_suggested_for_one_field_only()
    {
        // "prompt" fits both Instruction and (lower ranked) User-like fields; output must not reuse it.
        var mapping = ImportLayouts.SuggestMapping(["prompt", "completion"], DatasetType.Instruction);

        Assert.Equal("prompt", mapping[TargetField.Instruction]);
        Assert.Equal("completion", mapping[TargetField.Output]);
        Assert.False(mapping.ContainsKey(TargetField.Input));
    }

    [Theory]
    [InlineData(SourceLayout.OpenAiMessages, DatasetType.Chat, true)]
    [InlineData(SourceLayout.OpenAiMessages, DatasetType.Instruction, true)]
    [InlineData(SourceLayout.OpenAiMessages, DatasetType.Preference, false)]
    [InlineData(SourceLayout.OpenAiPreference, DatasetType.Preference, true)]
    [InlineData(SourceLayout.OpenAiPreference, DatasetType.Chat, false)]
    [InlineData(SourceLayout.Custom, DatasetType.Preference, true)]
    [InlineData(SourceLayout.Sections, DatasetType.Chat, true)]
    [InlineData(SourceLayout.Paragraphs, DatasetType.Preference, true)]
    public void Knows_which_layouts_fit_which_dataset_types(SourceLayout layout, DatasetType type, bool expected)
    {
        Assert.Equal(expected, ImportLayouts.Supports(layout, type));
    }

    [Fact]
    public void Text_layouts_map_the_heading_or_first_line_to_the_question()
    {
        Assert.Equal(
            new Dictionary<TargetField, string> { [TargetField.Instruction] = TextColumns.Heading, [TargetField.Output] = TextColumns.Content },
            ImportLayouts.SuggestTextMapping(SourceLayout.Sections, DatasetType.Instruction));
        Assert.Equal(
            new Dictionary<TargetField, string> { [TargetField.User] = TextColumns.FirstLine, [TargetField.Assistant] = TextColumns.Rest },
            ImportLayouts.SuggestTextMapping(SourceLayout.Paragraphs, DatasetType.Chat));
        Assert.Equal(
            new Dictionary<TargetField, string> { [TargetField.Prompt] = TextColumns.Heading, [TargetField.Chosen] = TextColumns.Content },
            ImportLayouts.SuggestTextMapping(SourceLayout.Sections, DatasetType.Preference));
        Assert.True(ImportLayouts.IsColumnBased(SourceLayout.Sections));
        Assert.Null(ImportLayouts.NaturalType(SourceLayout.Paragraphs));
    }

    [Fact]
    public void Speakers_of_a_log_are_told_apart_by_name_or_by_who_opens()
    {
        // "Müşteri Hizmetleri" is customer service: the agent, not the customer.
        Assert.Equal(
            new Dictionary<TargetField, string> { [TargetField.User] = "Müşteri", [TargetField.Assistant] = "Müşteri Hizmetleri" },
            ImportLayouts.SuggestSpeakers(["Müşteri Hizmetleri", "Müşteri"], new Dictionary<string, int> { ["Müşteri Hizmetleri"] = 3 }));
        Assert.Equal(
            new Dictionary<TargetField, string> { [TargetField.User] = "Zeynep", [TargetField.Assistant] = "Mehmet" },
            ImportLayouts.SuggestSpeakers(["Mehmet", "Zeynep"], new Dictionary<string, int> { ["Zeynep"] = 4, ["Mehmet"] = 1 }));

        // Two agents and no telling names: everyone but the customer answers.
        Assert.Equal(
            new Dictionary<TargetField, string> { [TargetField.User] = "Ali" },
            ImportLayouts.SuggestSpeakers(["Ali", "Veli", "Ayşe"], new Dictionary<string, int> { ["Ali"] = 2 }));
        Assert.Equal(
            [new TargetFieldInfo(TargetField.User, true), new TargetFieldInfo(TargetField.Assistant, false)],
            ImportLayouts.FieldsFor(DatasetType.Chat, SourceLayout.Transcript));
        Assert.Empty(ImportLayouts.FieldsFor(DatasetType.Preference, SourceLayout.Transcript));
        Assert.Equal(DatasetType.Chat, ImportLayouts.NaturalType(SourceLayout.Transcript));
    }

    [Theory]
    [InlineData("System Prompt", "systemprompt")]
    [InlineData("Sistem Talimatı", "sistemtalimati")]
    [InlineData("ÇIKTI", "cikti")]
    [InlineData("tercih_edilen", "tercihedilen")]
    public void Normalizes_column_names(string name, string expected)
    {
        Assert.Equal(expected, ImportLayouts.Normalize(name));
    }
}
