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
    public void Knows_which_layouts_fit_which_dataset_types(SourceLayout layout, DatasetType type, bool expected)
    {
        Assert.Equal(expected, ImportLayouts.Supports(layout, type));
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
