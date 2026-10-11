using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIDatasetStudio.Domain.Enums;

namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Fine-tuning tools that read their settings from a file (SDD §21 V4, M20).
/// </summary>
/// <remarks>
/// Values are persisted by name in export profiles; never rename existing members.
/// </remarks>
public enum TrainingFramework
{
    /// <summary>Axolotl: one YAML file, run with <c>axolotl train axolotl.yaml</c>.</summary>
    Axolotl,

    /// <summary>LLaMA-Factory: <c>dataset_info.json</c> next to the data and a YAML file, run with <c>llamafactory-cli train llamafactory.yaml</c>.</summary>
    LlamaFactory,
}

/// <summary>
/// A training configuration written next to the exported files.
/// </summary>
/// <param name="BaseModel">The Hugging Face id of the model to fine-tune, e.g. "Qwen/Qwen2.5-7B-Instruct".</param>
/// <param name="QLoRA">Load the base model in 4 bits and train the adapter on it (QLoRA, M26): far less
/// GPU memory, a little slower; needs bitsandbytes (its ROCm build on AMD).</param>
/// <param name="Gpu">Whose GPU trains (M27): AMD gets settings ROCm runs.</param>
/// <param name="GpuMemoryGb">The GPU's memory; sets the batch size. <c>null</c> when not given.</param>
/// <param name="SaveMemory">Fused Liger kernels and activations moved to the computer's memory between
/// layers (M27): the same training, in less GPU memory, a little slower.</param>
public sealed record TrainingOptions(
    TrainingFramework Framework, string BaseModel, bool QLoRA = false, GpuVendor Gpu = GpuVendor.Nvidia, int? GpuMemoryGb = null, bool SaveMemory = false);

/// <summary>
/// What a configuration is written for: the dataset, its export and the files it wrote.
/// </summary>
/// <param name="Files">The data files by split part (<c>null</c> for a single file), as names in the same folder.</param>
/// <param name="HasSystemPrompts">Some written record has a system prompt of its own (Alpaca's <c>system</c> field).</param>
public sealed record TrainingTarget(
    string DatasetName, DatasetType Type, ExportFormat Format, int ContextLength, IReadOnlyList<(SplitPart? Part, string FileName)> Files, int Seed, bool HasSystemPrompts = false);

public sealed record TrainingFile(string FileName, string Content);

/// <summary>
/// Renders ready-to-run LoRA fine-tuning configurations for exported files (M20, ADR-042). The
/// data format decides how the tool reads the files; the base model decides the chat template.
/// Settings are common starting points, marked in the file as such.
/// </summary>
public static class TrainingConfigs
{
    public const string AxolotlFile = "axolotl.yaml";
    public const string LlamaFactoryFile = "llamafactory.yaml";
    public const string LlamaFactoryDatasets = "dataset_info.json";

    /// <summary>
    /// The files a tool's configuration is written to, next to the data.
    /// </summary>
    public static IReadOnlyList<string> FileNames(TrainingFramework framework) =>
        framework == TrainingFramework.Axolotl ? [AxolotlFile] : [LlamaFactoryDatasets, LlamaFactoryFile];

    /// <summary>
    /// A model to start from that matches the format's chat template.
    /// </summary>
    public static string DefaultBaseModel(ExportFormat format) =>
        format == ExportFormat.Llama3 ? "meta-llama/Llama-3.1-8B-Instruct" : "Qwen/Qwen2.5-7B-Instruct";

    /// <summary>
    /// Whether the tool can train on files of this format. LLaMA-Factory has no reader for
    /// whole-conversation text (ChatML, Llama 3); neither reads OpenAI's preference shape.
    /// </summary>
    public static bool Supports(TrainingFramework framework, ExportFormat format, DatasetType type) =>
        ExportFormats.For(type).Contains(format)
        && (format, type, framework) switch
        {
            (ExportFormat.OpenAi, DatasetType.Preference, _) => false,
            (ExportFormat.ChatMl or ExportFormat.Llama3, _, TrainingFramework.LlamaFactory) => false,
            _ => true,
        };

    /// <summary>
    /// The files to write next to the data, by name.
    /// </summary>
    /// <exception cref="ArgumentException">The tool cannot read this format.</exception>
    public static IReadOnlyList<TrainingFile> Render(TrainingOptions training, TrainingTarget target)
    {
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(target);
        if (!Supports(training.Framework, target.Format, target.Type))
        {
            throw new ArgumentException($"{training.Framework} cannot train on {target.Format} files of a {target.Type} dataset.", nameof(training));
        }

        var model = string.IsNullOrWhiteSpace(training.BaseModel) ? DefaultBaseModel(target.Format) : training.BaseModel.Trim();
        return training.Framework switch
        {
            TrainingFramework.Axolotl => [new(AxolotlFile, Axolotl(model, target, training))],
            _ => [new(LlamaFactoryDatasets, LlamaFactoryDatasetInfo(target)), new(LlamaFactoryFile, LlamaFactory(model, target, training))],
        };
    }

    /// <summary>
    /// The chat template LLaMA-Factory needs for a model, from its name; "default" when unknown.
    /// </summary>
    public static string LlamaFactoryTemplate(string baseModel)
    {
        var name = baseModel.ToLowerInvariant();
        return name switch
        {
            _ when name.Contains("qwen3", StringComparison.Ordinal) => "qwen3",
            _ when name.Contains("qwen", StringComparison.Ordinal) => "qwen",
            _ when name.Contains("llama-3", StringComparison.Ordinal) || name.Contains("llama3", StringComparison.Ordinal) => "llama3",
            _ when name.Contains("gemma-3", StringComparison.Ordinal) || name.Contains("gemma3", StringComparison.Ordinal) => "gemma3",
            _ when name.Contains("gemma", StringComparison.Ordinal) => "gemma",
            _ when name.Contains("mistral", StringComparison.Ordinal) || name.Contains("mixtral", StringComparison.Ordinal) => "mistral",
            _ when name.Contains("phi-4", StringComparison.Ordinal) || name.Contains("phi4", StringComparison.Ordinal) => "phi4",
            _ when name.Contains("phi", StringComparison.Ordinal) => "phi",
            _ => "default",
        };
    }

    private static string Axolotl(string model, TrainingTarget target, TrainingOptions training)
    {
        var qlora = training.QLoRA;
        var yaml = new Yaml();
        Header(yaml, target, training, "axolotl train " + AxolotlFile);
        yaml.Line("base_model", Quote(model));
        if (target.Format is ExportFormat.OpenAi or ExportFormat.ShareGpt)
        {
            yaml.Comment("The model's own chat template formats the conversations.");
            yaml.Line("chat_template", "tokenizer_default");
        }

        if (target.Type == DatasetType.Preference)
        {
            yaml.Line("rl", "dpo");
        }

        yaml.Blank();
        AxolotlDatasets(yaml, "datasets", target, Train(target), model);

        // The split was made here; Axolotl does not split again.
        yaml.Line("val_set_size", "0");
        if (File(target, SplitPart.Validation) is { } validation)
        {
            AxolotlDatasets(yaml, "test_datasets", target, validation, model);
        }

        yaml.Blank();
        yaml.Line("sequence_len", Number(target.ContextLength));
        yaml.Line("sample_packing", target.Type == DatasetType.Preference ? "false" : "true");
        yaml.Line("pad_to_sequence_len", target.Type == DatasetType.Preference ? "false" : "true");
        yaml.Blank();
        if (qlora)
        {
            yaml.Comment("QLoRA: the model is loaded in 4 bits and a small adapter is trained on it.");
            yaml.Comment($"Needs {QLoRANeeds(training)}; the result is a LoRA adapter.");
            yaml.Line("adapter", "qlora");
            yaml.Line("load_in_4bit", "true");
        }
        else
        {
            yaml.Comment("LoRA: a small adapter is trained instead of the whole model.");
            yaml.Line("adapter", "lora");
        }

        yaml.Line("lora_r", "16");
        yaml.Line("lora_alpha", "32");
        yaml.Line("lora_dropout", "0.05");
        yaml.Line("lora_target_linear", "true");
        if (training.SaveMemory)
        {
            yaml.Blank();
            yaml.Comment("Memory saving: fused Liger kernels (pip install liger-kernel) and activations kept in");
            yaml.Comment("the computer's memory between layers. Same training, less GPU memory, a little slower.");
            yaml.Key("plugins");
            yaml.ListItem("axolotl.integrations.liger.LigerPlugin");
            yaml.Line("liger_rope", "true");
            yaml.Line("liger_rms_norm", "true");
            yaml.Line("liger_glu_activation", "true");

            // Preference training compares the answers' log-probabilities, which the fused loss does not give.
            if (target.Type != DatasetType.Preference)
            {
                yaml.Line("liger_fused_linear_cross_entropy", "true");
            }

            yaml.Line("activation_offloading", "true");
        }

        var (batch, accumulation) = TrainingMemory.Batch(training.GpuMemoryGb);
        yaml.Blank();
        yaml.Comment(training.GpuMemoryGb is { } memory
            ? $"Starting points for a {Number(memory)} GB GPU: adjust to your data."
            : "Starting points: adjust to your GPU and data.");
        yaml.Line("micro_batch_size", Number(batch));
        yaml.Line("gradient_accumulation_steps", Number(accumulation));
        yaml.Line("num_epochs", "3");
        yaml.Line("learning_rate", target.Type == DatasetType.Preference ? "0.000005" : "0.0002");
        // bitsandbytes' paged 8-bit optimizer and PyTorch's fused one are CUDA's; ROCm runs the plain one.
        yaml.Line("optimizer", training.Gpu == GpuVendor.Amd ? "adamw_torch" : qlora ? "paged_adamw_8bit" : "adamw_torch_fused");
        yaml.Line("lr_scheduler", "cosine");
        yaml.Line("warmup_ratio", "0.05");
        yaml.Line("bf16", "auto");
        yaml.Line("gradient_checkpointing", "true");
        yaml.Line("logging_steps", "10");
        if (File(target, SplitPart.Validation) is not null)
        {
            yaml.Line("evals_per_epoch", "2");
        }

        yaml.Line("saves_per_epoch", "1");
        yaml.Line("seed", Number(target.Seed));
        yaml.Line("output_dir", Quote("./outputs/" + Slug(target.DatasetName)));
        return yaml.ToString();
    }

    private static void AxolotlDatasets(Yaml yaml, string key, TrainingTarget target, string file, string model)
    {
        yaml.Key(key);
        yaml.Item("path", Quote(file));
        yaml.Field("ds_type", "json");
        yaml.Field("data_files", $"[{Quote(file)}]");

        // A JSON file is read as one split named "train", whatever the file is for.
        yaml.Field("split", "train");
        switch (target.Format)
        {
            case ExportFormat.OpenAi:
                yaml.Field("type", "chat_template");
                yaml.Field("field_messages", "messages");
                yaml.Field("message_property_mappings", "{role: role, content: content}");
                yaml.Field("roles_to_train", "[\"assistant\"]");
                break;
            case ExportFormat.ShareGpt:
                yaml.Field("type", "chat_template");
                yaml.Field("field_messages", "conversations");
                yaml.Field("message_property_mappings", "{role: from, content: value}");
                yaml.Field("roles", "{user: [\"human\"], assistant: [\"gpt\"], system: [\"system\"]}");
                yaml.Field("roles_to_train", "[\"assistant\"]");
                break;
            case ExportFormat.Alpaca:
                yaml.Field("type", "alpaca");
                break;
            case ExportFormat.HuggingFaceDpo:
                // Prompt, chosen and rejected texts, wrapped in the model family's chat markup.
                yaml.Field("type", LlamaFactoryTemplate(model) == "llama3" ? "llama3.prompt_pairs" : "chatml.prompt_pairs");
                break;
            default:
                // ChatML or Llama 3 text: the conversation is already in the model's markup.
                yaml.Field("type", "completion");
                yaml.Field("field", "text");
                break;
        }
    }

    private static string LlamaFactory(string model, TrainingTarget target, TrainingOptions training)
    {
        var qlora = training.QLoRA;
        var yaml = new Yaml();
        Header(yaml, target, training, "llamafactory-cli train " + LlamaFactoryFile);
        yaml.Comment("model");
        yaml.Line("model_name_or_path", Quote(model));
        yaml.Line("trust_remote_code", "true");
        if (training.SaveMemory)
        {
            yaml.Comment("memory saving: fused Liger kernels (pip install liger-kernel) and activations kept in the");
            yaml.Comment("computer's memory between layers; same training, less GPU memory, a little slower");
            yaml.Line("enable_liger_kernel", "true");
            yaml.Line("use_unsloth_gc", "true");
        }

        yaml.Blank();
        yaml.Comment(qlora
            ? $"method: QLoRA, the model is loaded in 4 bits and a small adapter is trained on it (needs {QLoRANeeds(training)})"
            : "method: LoRA, a small adapter is trained instead of the whole model");
        yaml.Line("stage", target.Type == DatasetType.Preference ? "dpo" : "sft");
        yaml.Line("do_train", "true");
        yaml.Line("finetuning_type", "lora");
        yaml.Line("lora_rank", "16");
        yaml.Line("lora_target", "all");
        if (qlora)
        {
            yaml.Line("quantization_bit", "4");
            yaml.Line("quantization_method", "bnb");
            yaml.Line("double_quantization", "true");
        }
        if (target.Type == DatasetType.Preference)
        {
            yaml.Line("pref_beta", "0.1");
            yaml.Line("pref_loss", "sigmoid");
        }

        yaml.Blank();
        yaml.Comment("dataset: described in " + LlamaFactoryDatasets + " in this folder");
        yaml.Line("dataset_dir", Quote("."));
        yaml.Line("dataset", DatasetName(target, SplitPart.Train));
        if (File(target, SplitPart.Validation) is not null)
        {
            yaml.Line("eval_dataset", DatasetName(target, SplitPart.Validation));
        }

        var template = LlamaFactoryTemplate(model);
        yaml.Line("template", template == "default" ? "default  # set your model's template" : template);
        yaml.Line("cutoff_len", Number(target.ContextLength));
        yaml.Line("preprocessing_num_workers", "8");
        yaml.Blank();
        yaml.Comment("output");
        yaml.Line("output_dir", Quote($"saves/{Slug(target.DatasetName)}/{(qlora ? "qlora" : "lora")}/{(target.Type == DatasetType.Preference ? "dpo" : "sft")}"));
        yaml.Line("logging_steps", "10");
        yaml.Line("save_strategy", "epoch");
        yaml.Line("plot_loss", "true");
        yaml.Blank();
        var (batch, accumulation) = TrainingMemory.Batch(training.GpuMemoryGb);
        yaml.Comment(training.GpuMemoryGb is { } memory
            ? $"train: starting points for a {Number(memory)} GB GPU, adjust to your data"
            : "train: starting points, adjust to your GPU and data");
        yaml.Line("per_device_train_batch_size", Number(batch));
        yaml.Line("gradient_accumulation_steps", Number(accumulation));
        yaml.Line("learning_rate", target.Type == DatasetType.Preference ? "5.0e-6" : "1.0e-4");
        yaml.Line("num_train_epochs", "3.0");
        yaml.Line("lr_scheduler_type", "cosine");
        yaml.Line("warmup_ratio", "0.1");
        yaml.Line("bf16", "true");
        yaml.Line("seed", Number(target.Seed));
        if (File(target, SplitPart.Validation) is not null)
        {
            yaml.Blank();
            yaml.Comment("eval");
            yaml.Line("per_device_eval_batch_size", "1");
            yaml.Line("eval_strategy", "epoch");
        }

        return yaml.ToString();
    }

    /// <summary>
    /// LLaMA-Factory's description of the data files: how to read each one.
    /// </summary>
    private static string LlamaFactoryDatasetInfo(TrainingTarget target)
    {
        var info = new JsonObject();
        foreach (var (part, file) in target.Files.Where(f => f.Part is null or SplitPart.Train or SplitPart.Validation))
        {
            var entry = new JsonObject { ["file_name"] = file };
            switch (target.Format)
            {
                case ExportFormat.OpenAi:
                    entry["formatting"] = "sharegpt";
                    entry["columns"] = new JsonObject { ["messages"] = "messages" };
                    entry["tags"] = new JsonObject
                    {
                        ["role_tag"] = "role",
                        ["content_tag"] = "content",
                        ["user_tag"] = "user",
                        ["assistant_tag"] = "assistant",
                        ["system_tag"] = "system",
                    };
                    break;
                case ExportFormat.ShareGpt:
                    entry["formatting"] = "sharegpt";
                    entry["columns"] = new JsonObject { ["messages"] = "conversations" };
                    entry["tags"] = new JsonObject
                    {
                        ["role_tag"] = "from",
                        ["content_tag"] = "value",
                        ["user_tag"] = "human",
                        ["assistant_tag"] = "gpt",
                        ["system_tag"] = "system",
                    };
                    break;
                case ExportFormat.Alpaca:
                    entry["formatting"] = "alpaca";
                    entry["columns"] = new JsonObject { ["prompt"] = "instruction", ["query"] = "input", ["response"] = "output" };
                    if (target.HasSystemPrompts)
                    {
                        // Read only when mapped; a record without one gets the template's default.
                        entry["columns"]!["system"] = "system";
                    }

                    break;
                default:
                    // Hugging Face DPO: a prompt with a better and a worse answer.
                    entry["formatting"] = "alpaca";
                    entry["ranking"] = true;
                    entry["columns"] = new JsonObject { ["prompt"] = "prompt", ["chosen"] = "chosen", ["rejected"] = "rejected" };
                    break;
            }

            info[DatasetName(target, part ?? SplitPart.Train)] = entry;
        }

        return info.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static void Header(Yaml yaml, TrainingTarget target, TrainingOptions training, string command)
    {
        yaml.Comment($"Fine-tuning \"{target.DatasetName}\" with LoRA, written by AI Dataset Studio.");
        yaml.Comment($"Run it from this folder: {command}");
        if (training.Gpu == GpuVendor.Amd)
        {
            yaml.Comment("AMD GPU (ROCm): install PyTorch's ROCm build (pytorch.org) before the tool, on Linux or WSL.");
        }

        if (File(target, SplitPart.Test) is { } test)
        {
            yaml.Comment($"{test} is held out: evaluate the trained model on it, not during training.");
        }

        yaml.Blank();
    }

    private static string QLoRANeeds(TrainingOptions training) =>
        training.Gpu == GpuVendor.Amd ? "bitsandbytes' ROCm build" : "bitsandbytes";

    private static string Train(TrainingTarget target) =>
        File(target, SplitPart.Train) ?? target.Files.First(f => f.Part is null).FileName;

    private static string? File(TrainingTarget target, SplitPart part) =>
        target.Files.FirstOrDefault(f => f.Part == part).FileName;

    private static string DatasetName(TrainingTarget target, SplitPart part) =>
        Slug(target.DatasetName).Replace('-', '_') + (part == SplitPart.Validation ? "_val" : "_train");

    /// <summary>
    /// "Destek SSS (v2)" → "destek-sss-v2": safe in paths and names, Turkish letters folded.
    /// </summary>
    public static string Slug(string name)
    {
        // Decomposed, "İ" and "ç" are "I" and "c" with a mark; the marks are dropped.
        var folded = name.Replace('ı', 'i').Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder();
        foreach (var c in folded)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var result = slug.ToString().Trim('-');
        return result.Length == 0 ? "dataset" : result;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Just enough YAML: top-level keys, comments and one list of mappings.
    /// </summary>
    private sealed class Yaml
    {
        private readonly StringBuilder _text = new();

        public void Line(string key, string value) => _text.Append(key).Append(": ").Append(value).Append('\n');

        public void Key(string key) => _text.Append(key).Append(":\n");

        public void Item(string key, string value) => _text.Append("  - ").Append(key).Append(": ").Append(value).Append('\n');

        public void ListItem(string value) => _text.Append("  - ").Append(value).Append('\n');

        public void Field(string key, string value) => _text.Append("    ").Append(key).Append(": ").Append(value).Append('\n');

        public void Comment(string text) => _text.Append("# ").Append(text.ReplaceLineEndings(" ")).Append('\n');

        public void Blank() => _text.Append('\n');

        public override string ToString() => _text.ToString();
    }
}
