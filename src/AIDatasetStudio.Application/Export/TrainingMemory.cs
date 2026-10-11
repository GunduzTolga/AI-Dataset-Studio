using System.Globalization;
using System.Text.RegularExpressions;

namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Whose GPU the configuration is written for (M27). Values are persisted by name in export
/// profiles; never rename existing members.
/// </summary>
public enum GpuVendor
{
    /// <summary>NVIDIA (CUDA): every option the tools have.</summary>
    Nvidia,

    /// <summary>AMD Radeon (ROCm): PyTorch's ROCm build; 4-bit loading needs bitsandbytes' ROCm build.</summary>
    Amd,
}

/// <summary>
/// How a training run is expected to sit in the GPU's memory.
/// </summary>
public enum MemoryFit
{
    Fits,
    Tight,
    TooBig,
}

/// <summary>
/// A rough estimate of the GPU memory a LoRA run needs, from the model's size in its name (M27,
/// ADR-047). It is meant to tell "fits", "tight" and "will not fit" apart, not to the gigabyte.
/// </summary>
/// <remarks>
/// Per billion parameters: the weights (2 bytes each in 16 bits, about 0.6 with 4-bit loading),
/// the rank-16 adapter with its gradients and optimizer state (about 0.1 GB) and the activations
/// kept between layers (0.07 GB, 0.02 GB when they are moved to the computer's memory). On top:
/// the logits of a 2048-token sequence over a large vocabulary (1.2 GB, 0.2 GB with fused
/// kernels) and the GPU runtime itself (0.8 GB). Batch size 1.
/// </remarks>
public static partial class TrainingMemory
{
    /// <summary>The sequence length the estimate is made for.</summary>
    public const int EstimatedSequenceLength = 2048;

    /// <summary>Common model sizes, in billions of parameters, smallest first.</summary>
    public static IReadOnlyList<double> CommonSizes { get; } = [0.5, 1.5, 3, 4, 7, 8, 14, 32, 70];

    /// <summary>
    /// The model's size in billions of parameters as its name gives it ("Qwen2.5-7B-Instruct" → 7,
    /// "Qwen3-30B-A3B" → 30); <c>null</c> when the name does not say.
    /// </summary>
    public static double? ParameterBillions(string? baseModel)
    {
        if (string.IsNullOrWhiteSpace(baseModel))
        {
            return null;
        }

        var name = baseModel[(baseModel.LastIndexOf('/') + 1)..];
        return SizeInName().Match(name) is { Success: true } match
            && double.TryParse(match.Groups[1].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)
            && size > 0
                ? size
                : null;
    }

    /// <summary>
    /// The GPU memory, in GB, a LoRA run on a model of this size is expected to need.
    /// </summary>
    public static double EstimateGb(double parameterBillions, bool qlora, bool saveMemory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parameterBillions);
        var weights = parameterBillions * (qlora ? 0.6 : 2.0);
        var adapter = parameterBillions * 0.1;
        var activations = parameterBillions * (saveMemory ? 0.02 : 0.07);
        var logits = saveMemory ? 0.2 : 1.2;
        return weights + adapter + activations + logits + 0.8;
    }

    /// <summary>
    /// Whether an estimate fits a GPU: with room to spare, only just, or not at all.
    /// </summary>
    public static MemoryFit Fit(double estimateGb, int gpuMemoryGb) =>
        estimateGb <= gpuMemoryGb * 0.85 ? MemoryFit.Fits
        : estimateGb <= gpuMemoryGb ? MemoryFit.Tight
        : MemoryFit.TooBig;

    /// <summary>
    /// The largest common model size that fits a GPU with 4-bit loading and memory saving; <c>null</c>
    /// when not even the smallest does.
    /// </summary>
    public static double? LargestFitting(int gpuMemoryGb) =>
        CommonSizes.LastOrDefault(size => Fit(EstimateGb(size, qlora: true, saveMemory: true), gpuMemoryGb) != MemoryFit.TooBig) is var size and > 0
            ? size
            : null;

    /// <summary>
    /// Per-device batch size and gradient accumulation for a GPU: 8 sequences a step either way,
    /// fewer at a time on a smaller GPU. <c>null</c> memory keeps the general starting point.
    /// </summary>
    public static (int BatchSize, int Accumulation) Batch(int? gpuMemoryGb) => gpuMemoryGb switch
    {
        null => (2, 4),
        < 16 => (1, 8),
        < 24 => (2, 4),
        _ => (4, 2),
    };

    // "7B", "1.5B", "8b", "30B" in "30B-A3B": a number not glued to a letter or digit before it.
    [GeneratedRegex(@"(?<![\p{L}\d.])(\d+(?:\.\d+)?)[bB](?![\p{L}\d])")]
    private static partial Regex SizeInName();
}
