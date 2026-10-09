using System.Text.Json.Nodes;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Tests.Export;

/// <summary>
/// Fine-tuning configurations written next to exported files (M20, ADR-042).
/// </summary>
public sealed class TrainingConfigsTests
{
    private static readonly (SplitPart?, string)[] Split = [(SplitPart.Train, "train.jsonl"), (SplitPart.Validation, "val.jsonl"), (SplitPart.Test, "test.jsonl")];

    [Fact]
    public void Axolotl_trains_on_the_training_file_evaluates_on_validation_and_holds_out_the_test_file()
    {
        var file = Assert.Single(TrainingConfigs.Render(
            new TrainingOptions(TrainingFramework.Axolotl, " Qwen/Qwen2.5-7B-Instruct "),
            new TrainingTarget("Destek SSS (İade)", DatasetType.Chat, ExportFormat.OpenAi, 4096, Split, 7)));

        Assert.Equal(TrainingConfigs.AxolotlFile, file.FileName);
        Assert.Equal("""
            # Fine-tuning "Destek SSS (İade)" with LoRA, written by AI Dataset Studio.
            # Run it from this folder: axolotl train axolotl.yaml
            # test.jsonl is held out: evaluate the trained model on it, not during training.

            base_model: "Qwen/Qwen2.5-7B-Instruct"
            # The model's own chat template formats the conversations.
            chat_template: tokenizer_default

            datasets:
              - path: "train.jsonl"
                ds_type: json
                data_files: ["train.jsonl"]
                split: train
                type: chat_template
                field_messages: messages
                message_property_mappings: {role: role, content: content}
                roles_to_train: ["assistant"]
            val_set_size: 0
            test_datasets:
              - path: "val.jsonl"
                ds_type: json
                data_files: ["val.jsonl"]
                split: train
                type: chat_template
                field_messages: messages
                message_property_mappings: {role: role, content: content}
                roles_to_train: ["assistant"]

            sequence_len: 4096
            sample_packing: true
            pad_to_sequence_len: true

            # LoRA: a small adapter is trained instead of the whole model.
            adapter: lora
            lora_r: 16
            lora_alpha: 32
            lora_dropout: 0.05
            lora_target_linear: true

            # Starting points: adjust to your GPU and data.
            micro_batch_size: 2
            gradient_accumulation_steps: 4
            num_epochs: 3
            learning_rate: 0.0002
            optimizer: adamw_torch_fused
            lr_scheduler: cosine
            warmup_ratio: 0.05
            bf16: auto
            gradient_checkpointing: true
            logging_steps: 10
            evals_per_epoch: 2
            saves_per_epoch: 1
            seed: 7
            output_dir: "./outputs/destek-sss-iade"

            """, file.Content);
    }

    [Theory]
    [InlineData(ExportFormat.ShareGpt, "type: chat_template|field_messages: conversations|message_property_mappings: {role: from, content: value}|roles: {user: [\"human\"], assistant: [\"gpt\"], system: [\"system\"]}")]
    [InlineData(ExportFormat.Alpaca, "type: alpaca")]
    [InlineData(ExportFormat.ChatMl, "type: completion|field: text")]
    [InlineData(ExportFormat.Llama3, "type: completion|field: text")]
    public void Axolotl_reads_each_format_its_own_way(ExportFormat format, string lines)
    {
        var content = TrainingConfigs.Render(
            new TrainingOptions(TrainingFramework.Axolotl, ""), new TrainingTarget("Kargo", DatasetType.Instruction, format, 2048, [(null, "kargo.jsonl")], 42))[0].Content;

        Assert.All(lines.Split('|'), line => Assert.Contains("    " + line + "\n", content, StringComparison.Ordinal));
        Assert.Contains("base_model: \"" + TrainingConfigs.DefaultBaseModel(format) + "\"\n", content, StringComparison.Ordinal);
        Assert.Equal(format is ExportFormat.ShareGpt, content.Contains("chat_template: tokenizer_default", StringComparison.Ordinal));
        Assert.DoesNotContain("test_datasets", content, StringComparison.Ordinal);
        Assert.DoesNotContain("evals_per_epoch", content, StringComparison.Ordinal);
    }

    [Fact]
    public void LLaMA_Factory_describes_the_files_and_trains_preference_pairs_with_dpo()
    {
        var files = TrainingConfigs.Render(
            new TrainingOptions(TrainingFramework.LlamaFactory, "meta-llama/Llama-3.1-8B-Instruct"),
            new TrainingTarget("Çağrı Merkezi", DatasetType.Preference, ExportFormat.HuggingFaceDpo, 8192, Split, 42));

        Assert.Equal([TrainingConfigs.LlamaFactoryDatasets, TrainingConfigs.LlamaFactoryFile], files.Select(f => f.FileName));
        var info = JsonNode.Parse(files[0].Content)!.AsObject();
        Assert.Equal(["cagri_merkezi_train", "cagri_merkezi_val"], info.Select(p => p.Key));
        var train = info["cagri_merkezi_train"]!;
        Assert.Equal(("train.jsonl", "alpaca", true), ((string?)train["file_name"], (string?)train["formatting"], (bool?)train["ranking"]));
        Assert.Equal(("prompt", "chosen", "rejected"), ((string?)train["columns"]!["prompt"], (string?)train["columns"]!["chosen"], (string?)train["columns"]!["rejected"]));

        var yaml = files[1].Content;
        foreach (var line in new[]
        {
            "model_name_or_path: \"meta-llama/Llama-3.1-8B-Instruct\"", "stage: dpo", "pref_beta: 0.1", "dataset_dir: \".\"",
            "dataset: cagri_merkezi_train", "eval_dataset: cagri_merkezi_val", "template: llama3", "cutoff_len: 8192",
            "output_dir: \"saves/cagri-merkezi/lora/dpo\"", "eval_strategy: epoch",
        })
        {
            Assert.Contains(line + "\n", yaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LLaMA_Factory_reads_openai_messages_and_alpaca_columns()
    {
        var info = (string format) => JsonNode.Parse(TrainingConfigs.Render(
            new TrainingOptions(TrainingFramework.LlamaFactory, "Qwen/Qwen3-8B"),
            new TrainingTarget("SSS", DatasetType.Instruction, Enum.Parse<ExportFormat>(format), 4096, [(null, "sss.jsonl")], 42))[0].Content)!["sss_train"]!;

        var openAI = info("OpenAi");
        Assert.Equal(("sss.jsonl", "sharegpt", "messages"), ((string?)openAI["file_name"], (string?)openAI["formatting"], (string?)openAI["columns"]!["messages"]));
        Assert.Equal(("role", "content", "user", "assistant", "system"), (
            (string?)openAI["tags"]!["role_tag"], (string?)openAI["tags"]!["content_tag"], (string?)openAI["tags"]!["user_tag"],
            (string?)openAI["tags"]!["assistant_tag"], (string?)openAI["tags"]!["system_tag"]));

        var alpaca = info("Alpaca");
        Assert.Equal(("alpaca", "instruction", "input", "output"), (
            (string?)alpaca["formatting"], (string?)alpaca["columns"]!["prompt"], (string?)alpaca["columns"]!["query"], (string?)alpaca["columns"]!["response"]));
    }

    [Fact]
    public void Each_tool_takes_the_formats_it_can_read_and_its_templates_follow_the_model()
    {
        Assert.True(TrainingConfigs.Supports(TrainingFramework.Axolotl, ExportFormat.ChatMl, DatasetType.Chat));
        Assert.False(TrainingConfigs.Supports(TrainingFramework.LlamaFactory, ExportFormat.ChatMl, DatasetType.Chat));
        Assert.False(TrainingConfigs.Supports(TrainingFramework.LlamaFactory, ExportFormat.Llama3, DatasetType.Instruction));
        Assert.False(TrainingConfigs.Supports(TrainingFramework.Axolotl, ExportFormat.OpenAi, DatasetType.Preference));
        Assert.True(TrainingConfigs.Supports(TrainingFramework.Axolotl, ExportFormat.HuggingFaceDpo, DatasetType.Preference));
        Assert.False(TrainingConfigs.Supports(TrainingFramework.Axolotl, ExportFormat.HuggingFaceDpo, DatasetType.Chat));
        Assert.Throws<ArgumentException>(() => TrainingConfigs.Render(
            new TrainingOptions(TrainingFramework.LlamaFactory, ""), new TrainingTarget("x", DatasetType.Chat, ExportFormat.ChatMl, 4096, [(null, "x.jsonl")], 42)));

        Assert.Equal(
            ["qwen3", "qwen", "llama3", "gemma3", "mistral", "phi4", "default"],
            new[] { "Qwen/Qwen3-8B", "Qwen/Qwen2.5-7B-Instruct", "meta-llama/Llama-3.1-8B-Instruct", "google/gemma-3-12b-it", "mistralai/Mistral-7B-Instruct-v0.3", "microsoft/phi-4", "ytu-ce-cosmos/Turkish-Llama-8b" }
                .Select(TrainingConfigs.LlamaFactoryTemplate));
        Assert.Equal("meta-llama/Llama-3.1-8B-Instruct", TrainingConfigs.DefaultBaseModel(ExportFormat.Llama3));
        Assert.Equal(["destek-sss-iade", "cagri-merkezi-g-s-u-o-i", "dataset"], new[] { "Destek SSS (İade)", "Çağrı Merkezi — Ğ/Ş/Ü/Ö/ı", " " }.Select(TrainingConfigs.Slug));
    }
}
