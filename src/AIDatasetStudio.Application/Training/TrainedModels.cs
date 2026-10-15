using System.Text;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;

namespace AIDatasetStudio.Application.Training;

/// <summary>
/// The LoRA adapter a LLaMA-Factory training saved in an exported folder, with what inference
/// needs to load it the way it was trained (M30, ADR-051).
/// </summary>
/// <param name="Folder">The exported folder the training ran in.</param>
/// <param name="BaseModel">The model the adapter was trained on (<c>model_name_or_path</c>).</param>
/// <param name="AdapterDirectory">Where the adapter was saved (<c>output_dir</c>), relative to <paramref name="Folder"/>.</param>
/// <param name="Settings">The training settings inference reads too (chat template, 4-bit loading), in order.</param>
/// <param name="BaseOnly">The base model as it was before training: served the same way, without the adapter,
/// so its answers compare with the trained model's.</param>
public sealed record TrainedAdapter(string Folder, string BaseModel, string AdapterDirectory, IReadOnlyList<KeyValuePair<string, string>> Settings, bool BaseOnly = false)
{
    /// <summary>
    /// Whether the base model is loaded in 4 bits, as QLoRA trained it.
    /// </summary>
    public bool IsQLoRA => Settings.Any(s => s.Key == "quantization_bit");

    /// <summary>
    /// "Qwen2.5-7B-Instruct + LoRA (destek)", or "Qwen2.5-7B-Instruct (4 bit)" for the base model:
    /// the name the model is served and evaluated under.
    /// </summary>
    public string Name => BaseOnly
        ? TrainedAdapters.ShortName(BaseModel) + (IsQLoRA ? " (4 bit)" : string.Empty)
        : $"{TrainedAdapters.ShortName(BaseModel)} + {(IsQLoRA ? "QLoRA" : "LoRA")} ({Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))})";

    /// <summary>
    /// The model the adapter was trained on, loaded the same way but without it.
    /// </summary>
    public TrainedAdapter WithoutAdapter() => this with { BaseOnly = true };
}

/// <summary>
/// Reads a training's adapter from the LLaMA-Factory configuration the export wrote, and writes
/// the configuration that serves it (M30).
/// </summary>
public static class TrainedAdapters
{
    /// <summary>
    /// The configuration that serves the adapter, written next to the training's.
    /// </summary>
    public const string ServeFile = "llamafactory-serve.yaml";

    /// <summary>
    /// The file a finished LoRA training leaves in its output folder.
    /// </summary>
    public const string AdapterFile = "adapter_config.json";

    // The training settings inference needs as well; LLaMA-Factory refuses keys it does not use
    // for inference (dataset, output_dir, learning rate), so only these are carried over.
    private static readonly string[] InferenceKeys =
        ["trust_remote_code", "finetuning_type", "quantization_bit", "quantization_method", "double_quantization", "template"];

    /// <summary>
    /// The adapter a LLaMA-Factory configuration trains; <c>null</c> when it names no model or
    /// output folder. Whether the adapter was saved yet is not checked here.
    /// </summary>
    public static TrainedAdapter? Read(string folder, string configuration)
    {
        var values = Values(configuration);
        if (!values.TryGetValue("model_name_or_path", out var model) || model.Length == 0
            || !values.TryGetValue("output_dir", out var output) || output.Length == 0)
        {
            return null;
        }

        var settings = InferenceKeys
            .Where(values.ContainsKey)
            .Select(key => KeyValuePair.Create(key, values[key]))
            .ToList();
        return new TrainedAdapter(folder, model, output, settings);
    }

    /// <summary>
    /// The configuration for <c>llamafactory-cli api</c>: the base model, the adapter on it and
    /// the settings it was trained with. <c>llamafactory-cli chat</c> reads it too.
    /// </summary>
    public static string ServeConfig(TrainedAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var yaml = new StringBuilder()
            .Append("# Serves the adapter trained in this folder, for AI Dataset Studio's Tools > Evaluate a Model.\n")
            .Append("# Written by AI Dataset Studio from ").Append(TrainingConfigs.LlamaFactoryFile).Append(". To talk to the trained model:\n")
            .Append("#   llamafactory-cli chat ").Append(ServeFile).Append('\n')
            .Append("model_name_or_path: ").Append(Quote(adapter.BaseModel)).Append('\n');
        if (!adapter.BaseOnly)
        {
            yaml.Append("adapter_name_or_path: ").Append(Quote(adapter.AdapterDirectory)).Append('\n');
        }

        foreach (var (key, value) in adapter.Settings)
        {
            yaml.Append(key).Append(": ").Append(key == "template" ? value : Scalar(value)).Append('\n');
        }

        return yaml.Append("infer_backend: huggingface\n").ToString();
    }

    /// <summary>
    /// The last part of a model's name or path: "Qwen2.5-7B-Instruct" for "Qwen/Qwen2.5-7B-Instruct".
    /// </summary>
    public static string ShortName(string model)
    {
        var trimmed = model.Trim().TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    // The top-level "key: value" lines, without comments and quotes. Enough for the files the
    // export writes, and for the same files edited by hand.
    private static Dictionary<string, string> Values(string configuration)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in configuration.Split('\n'))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#' || line.IndexOf(':', StringComparison.Ordinal) is not (> 0 and var colon))
            {
                continue;
            }

            values[line[..colon].Trim()] = Unquote(line[(colon + 1)..].Trim());
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value.IndexOf('"', 1) > 0)
        {
            var text = new StringBuilder();
            for (var i = 1; i < value.Length && value[i] != '"'; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    i++;
                }

                text.Append(value[i]);
            }

            return text.ToString();
        }

        if (value.Length >= 2 && value[0] == '\'' && value.IndexOf('\'', 1) is > 0 and var end)
        {
            return value[1..end];
        }

        // "default  # set your model's template": a comment starts after white space.
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Scalar(string value) => value.Length == 0 || value.IndexOfAny([':', '#', '"', '\'']) >= 0 ? Quote(value) : value;
}

/// <summary>
/// Serves a trained adapter on this computer with LLaMA-Factory's OpenAI-compatible API, so
/// the model evaluation asks it the dataset's questions like any other model (M30, ADR-051).
/// </summary>
public interface IModelServer
{
    /// <summary>
    /// The adapter a folder's LLaMA-Factory training saved; <c>null</c> when the folder has no
    /// LLaMA-Factory configuration or its training has not saved an adapter yet.
    /// </summary>
    TrainedAdapter? FindAdapter(string folder);

    /// <summary>
    /// Starts <c>llamafactory-cli api</c> on the adapter, reachable from this computer only, and
    /// returns once it answers. Loading the model can take minutes; <paramref name="log"/> gets
    /// the tool's lines meanwhile.
    /// </summary>
    /// <exception cref="ModelServerException">The tool could not be started, or it stopped before it answered.</exception>
    Task<IServedModel> StartAsync(TrainedAdapter adapter, string toolPath, IProgress<string> log, CancellationToken cancellationToken = default);
}

/// <summary>
/// A trained model being served. Disposing it stops the tool and everything it started.
/// </summary>
public interface IServedModel : IDisposable
{
    TrainedAdapter Adapter { get; }

    /// <summary>
    /// The connection that reaches it: the local address, the served name and a key only this
    /// app knows. It is not kept with the user's connections.
    /// </summary>
    AIConnection Connection { get; }

    /// <summary>
    /// Whether the tool has stopped, e.g. after running out of memory on a long answer.
    /// </summary>
    bool HasExited { get; }
}

/// <summary>
/// The trained model could not be served: the likely cause and the tool's last lines, to show with it.
/// </summary>
public sealed class ModelServerException(TrainingFailure failure, IReadOnlyList<string> lastLines, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public TrainingFailure Failure { get; } = failure;

    public IReadOnlyList<string> LastLines { get; } = lastLines;
}
