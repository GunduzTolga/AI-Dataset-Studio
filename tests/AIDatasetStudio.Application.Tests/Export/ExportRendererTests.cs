using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Tests.Export;

/// <summary>
/// Golden lines: the exact text each format writes (SDD §13.1).
/// </summary>
public sealed class ExportRendererTests
{
    private static readonly InstructionPayload Instruction = new("Başkent neresi?", "Türkiye için", "Ankara.");

    private static readonly ChatPayload Chat = new("Kısa yanıt ver.",
    [
        new ChatMessage(ChatRole.User, "Siparişim nerede?"),
        new ChatMessage(ChatRole.Assistant, "Yolda, \"bugün\" gelir."),
    ]);

    private static readonly PreferencePayload Preference = new("Başkent?", "Ankara.", "İstanbul.");

    [Fact]
    public void OpenAi_writes_messages_with_the_input_below_the_instruction()
    {
        Assert.Equal(
            """{"messages":[{"role":"user","content":"Başkent neresi?\n\nTürkiye için"},{"role":"assistant","content":"Ankara."}]}""",
            ExportRenderer.Render(ExportFormat.OpenAi, Instruction));
        Assert.Equal(
            """{"messages":[{"role":"user","content":"Soru?"},{"role":"assistant","content":"Cevap."}]}""",
            ExportRenderer.Render(ExportFormat.OpenAi, new InstructionPayload("Soru?", "  ", "Cevap.")));
    }

    [Fact]
    public void OpenAi_writes_the_system_prompt_first_and_escapes_quotes()
    {
        Assert.Equal(
            """{"messages":[{"role":"system","content":"Kısa yanıt ver."},{"role":"user","content":"Siparişim nerede?"},{"role":"assistant","content":"Yolda, \"bugün\" gelir."}]}""",
            ExportRenderer.Render(ExportFormat.OpenAi, Chat));
    }

    [Fact]
    public void OpenAi_writes_preference_pairs_in_its_dpo_shape()
    {
        Assert.Equal(
            """{"input":{"messages":[{"role":"user","content":"Başkent?"}]},"preferred_output":[{"role":"assistant","content":"Ankara."}],"non_preferred_output":[{"role":"assistant","content":"İstanbul."}]}""",
            ExportRenderer.Render(ExportFormat.OpenAi, Preference));
    }

    [Fact]
    public void ShareGpt_uses_human_and_gpt()
    {
        Assert.Equal(
            """{"conversations":[{"from":"system","value":"Kısa yanıt ver."},{"from":"human","value":"Siparişim nerede?"},{"from":"gpt","value":"Yolda, \"bugün\" gelir."}]}""",
            ExportRenderer.Render(ExportFormat.ShareGpt, Chat));
    }

    [Fact]
    public void Alpaca_keeps_instruction_fields_and_takes_single_turn_chats()
    {
        Assert.Equal(
            """{"instruction":"Başkent neresi?","input":"Türkiye için","output":"Ankara."}""",
            ExportRenderer.Render(ExportFormat.Alpaca, Instruction));
        Assert.Equal(
            """{"instruction":"Siparişim nerede?","input":"","output":"Yolda, \"bugün\" gelir.","system":"Kısa yanıt ver."}""",
            ExportRenderer.Render(ExportFormat.Alpaca, Chat));
    }

    [Fact]
    public void HuggingFace_dpo_writes_prompt_chosen_rejected()
    {
        Assert.Equal(
            """{"prompt":"Başkent?","chosen":"Ankara.","rejected":"İstanbul."}""",
            ExportRenderer.Render(ExportFormat.HuggingFaceDpo, Preference));
    }

    [Fact]
    public void Text_templates_follow_chatml_and_llama_3()
    {
        Assert.Equal(
            """{"text":"<|im_start|>system\nKısa yanıt ver.<|im_end|>\n<|im_start|>user\nSiparişim nerede?<|im_end|>\n<|im_start|>assistant\nYolda, \"bugün\" gelir.<|im_end|>\n"}""",
            ExportRenderer.Render(ExportFormat.ChatMl, Chat));
        Assert.Equal(
            """{"text":"<|begin_of_text|><|start_header_id|>user<|end_header_id|>\n\nBaşkent neresi?\n\nTürkiye için<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\nAnkara.<|eot_id|>"}""",
            ExportRenderer.Render(ExportFormat.Llama3, Instruction));
    }

    [Fact]
    public void Reasoning_is_its_own_field_in_openai_and_a_think_block_before_the_answer_elsewhere()
    {
        var instruction = new InstructionPayload("2+2?", "", "4", "İki artı iki.");
        var chat = new ChatPayload(null, [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4", "İki artı iki.")]);

        // Axolotl and Qwen3-style chat templates read reasoning_content.
        Assert.Equal(
            """{"messages":[{"role":"user","content":"2+2?"},{"role":"assistant","content":"4","reasoning_content":"İki artı iki."}]}""",
            ExportRenderer.Render(ExportFormat.OpenAi, chat));
        Assert.Equal(ExportRenderer.Render(ExportFormat.OpenAi, chat), ExportRenderer.Render(ExportFormat.OpenAi, instruction));

        // LLaMA-Factory's reasoning templates read the block in front of the answer.
        Assert.Equal(
            """{"conversations":[{"from":"human","value":"2+2?"},{"from":"gpt","value":"<think>\nİki artı iki.\n</think>\n\n4"}]}""",
            ExportRenderer.Render(ExportFormat.ShareGpt, chat));
        Assert.Equal(
            """{"instruction":"2+2?","input":"","output":"<think>\nİki artı iki.\n</think>\n\n4"}""",
            ExportRenderer.Render(ExportFormat.Alpaca, instruction));
        Assert.Equal(ExportRenderer.Render(ExportFormat.Alpaca, instruction), ExportRenderer.Render(ExportFormat.Alpaca, chat));
        Assert.Equal(
            """{"text":"<|im_start|>user\n2+2?<|im_end|>\n<|im_start|>assistant\n<think>\nİki artı iki.\n</think>\n\n4<|im_end|>\n"}""",
            ExportRenderer.Render(ExportFormat.ChatMl, instruction));
        Assert.Contains(
            "<|start_header_id|>assistant<|end_header_id|>\\n\\n<think>\\nİki artı iki.\\n</think>\\n\\n4<|eot_id|>",
            ExportRenderer.Render(ExportFormat.Llama3, chat),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_never_contain_line_breaks()
    {
        var multiline = new InstructionPayload("Satır 1\nSatır 2\r\n", "", "```\nkod\n```");

        foreach (var format in ExportFormats.For(DatasetType.Instruction))
        {
            var line = ExportRenderer.Render(format, multiline);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
        }
    }

    [Fact]
    public void Formats_follow_the_compatibility_matrix()
    {
        Assert.Equal([ExportFormat.HuggingFaceDpo, ExportFormat.OpenAi], ExportFormats.For(DatasetType.Preference));
        Assert.Contains(ExportFormat.Alpaca, ExportFormats.For(DatasetType.Chat));
        Assert.True(ExportFormats.IsLossy(ExportFormat.Alpaca, DatasetType.Chat));
        Assert.False(ExportFormats.IsLossy(ExportFormat.Alpaca, DatasetType.Instruction));

        var longChat = new ChatPayload(null,
        [
            new ChatMessage(ChatRole.User, "a"), new ChatMessage(ChatRole.Assistant, "b"),
            new ChatMessage(ChatRole.User, "c"), new ChatMessage(ChatRole.Assistant, "d"),
        ]);
        Assert.False(ExportFormats.CanWrite(ExportFormat.Alpaca, longChat));
        Assert.True(ExportFormats.CanWrite(ExportFormat.ShareGpt, longChat));
        Assert.False(ExportFormats.CanWrite(ExportFormat.ShareGpt, Preference));
        Assert.False(ExportFormats.CanWrite(ExportFormat.HuggingFaceDpo, Instruction));
        Assert.Throws<ArgumentException>(() => ExportRenderer.Render(ExportFormat.Alpaca, longChat));
    }

    [Fact]
    public void Split_files_are_named_after_their_part()
    {
        var files = ExportFormats.Files(ExportOptions.DefaultFor(ExportFormat.OpenAi) with { Split = SplitOptions.TrainValidation }, "out");

        Assert.Equal([(SplitPart.Train, Path.Combine("out", "train.jsonl")), (SplitPart.Validation, Path.Combine("out", "val.jsonl"))], files.Select(f => (f.Part!.Value, f.Path)));
        Assert.Equal([((SplitPart?)null, "faq.jsonl")], ExportFormats.Files(ExportOptions.DefaultFor(ExportFormat.OpenAi), "faq.jsonl"));
    }
}
