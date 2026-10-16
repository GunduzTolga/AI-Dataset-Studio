using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Tests.Training;

/// <summary>
/// Serving the adapter a training saved (M30): what is read from the training's configuration,
/// and the configuration that serves it.
/// </summary>
public sealed class TrainedAdaptersTests
{
    [Fact]
    public void The_adapter_is_served_with_the_model_template_and_4_bit_loading_it_was_trained_with()
    {
        var training = new TrainingOptions(TrainingFramework.LlamaFactory, "Qwen/Qwen2.5-7B-Instruct", QLoRA: true);
        var target = new TrainingTarget("Müşteri Destek", DatasetType.Chat, ExportFormat.ShareGpt, 4096, [(SplitPart.Train, "train.jsonl"), (SplitPart.Test, "test.jsonl")], 42);
        var config = TrainingConfigs.Render(training, target).Single(f => f.FileName == TrainingConfigs.LlamaFactoryFile).Content;
        var folder = Path.Combine("data", "destek");

        var adapter = TrainedAdapters.Read(folder, config)!;

        Assert.Equal(("Qwen/Qwen2.5-7B-Instruct", "saves/musteri-destek/qlora/sft", true), (adapter.BaseModel, adapter.AdapterDirectory, adapter.IsQLoRA));
        Assert.Equal("Qwen2.5-7B-Instruct + QLoRA (destek)", adapter.Name);
        Assert.Equal(
            """
            model_name_or_path: "Qwen/Qwen2.5-7B-Instruct"
            adapter_name_or_path: "saves/musteri-destek/qlora/sft"
            trust_remote_code: true
            finetuning_type: lora
            quantization_bit: 4
            quantization_method: bnb
            double_quantization: true
            template: qwen
            infer_backend: huggingface

            """.ReplaceLineEndings("\n"),
            string.Join('\n', TrainedAdapters.ServeConfig(adapter).Split('\n').Where(l => !l.StartsWith('#'))));

        // Before training: the same model, loaded the same way, without the adapter.
        var before = adapter.WithoutAdapter();
        Assert.Equal("Qwen2.5-7B-Instruct (4 bit)", before.Name);
        Assert.DoesNotContain("adapter_name_or_path", TrainedAdapters.ServeConfig(before), StringComparison.Ordinal);
        Assert.Contains("quantization_bit: 4\n", TrainedAdapters.ServeConfig(before), StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_edited_by_hand_is_read_without_its_comments_and_quotes()
    {
        const string config = """
            # my run
            model_name_or_path: '/models/Llama 3.1 8B'
            finetuning_type: lora   # LoRA
            template: default  # set your model's template
            output_dir: "out/\"run\" 1"
            dataset: destek_train
              nested: ignored
            """;

        var adapter = TrainedAdapters.Read("egitim", config)!;

        Assert.Equal(("/models/Llama 3.1 8B", "out/\"run\" 1", false), (adapter.BaseModel, adapter.AdapterDirectory, adapter.IsQLoRA));
        Assert.Equal([KeyValuePair.Create("finetuning_type", "lora"), KeyValuePair.Create("template", "default")], adapter.Settings);
        Assert.Equal("Llama 3.1 8B + LoRA (egitim)", adapter.Name);
        Assert.Contains("adapter_name_or_path: \"out/\\\"run\\\" 1\"\n", TrainedAdapters.ServeConfig(adapter), StringComparison.Ordinal);
        Assert.Null(TrainedAdapters.Read("egitim", "model_name_or_path: x\n"));
        Assert.Null(TrainedAdapters.Read("egitim", "output_dir: saves\n"));
    }
}
