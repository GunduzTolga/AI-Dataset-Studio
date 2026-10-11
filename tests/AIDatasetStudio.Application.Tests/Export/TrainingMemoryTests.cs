using AIDatasetStudio.Application.Export;

namespace AIDatasetStudio.Application.Tests.Export;

/// <summary>
/// The GPU memory estimate for a training run (M27, ADR-047).
/// </summary>
public sealed class TrainingMemoryTests
{
    [Theory]
    [InlineData("Qwen/Qwen2.5-7B-Instruct", 7)]
    [InlineData("meta-llama/Llama-3.1-8B-Instruct", 8)]
    [InlineData("Qwen/Qwen2.5-0.5B", 0.5)]
    [InlineData("HuggingFaceTB/SmolLM2-1.7B-Instruct", 1.7)]
    [InlineData("ytu-ce-cosmos/Turkish-Llama-8b-Instruct-v0.1", 8)]
    [InlineData("google/gemma-3-4b-it", 4)]
    [InlineData("Qwen/Qwen3-30B-A3B", 30)]
    [InlineData("openai/gpt-oss-20b", 20)]
    public void The_size_is_read_from_the_model_name(string model, double billions) =>
        Assert.Equal(billions, TrainingMemory.ParameterBillions(model));

    [Theory]
    [InlineData("microsoft/Phi-3.5-mini-instruct")]
    [InlineData("mistralai/Mixtral-8x7B-v0.1")]
    [InlineData("12B-labs/tiny-model")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_without_a_size_gives_none(string? model) =>
        Assert.Null(TrainingMemory.ParameterBillions(model));

    [Fact]
    public void Four_bit_loading_and_memory_saving_bring_a_7B_model_within_reach_of_small_GPUs()
    {
        // LoRA on a 7B model needs a 24 GB GPU; QLoRA only just fits 8 GB, comfortably with memory saving.
        Assert.Equal(MemoryFit.TooBig, TrainingMemory.Fit(TrainingMemory.EstimateGb(7, qlora: false, saveMemory: false), 16));
        Assert.Equal(MemoryFit.Fits, TrainingMemory.Fit(TrainingMemory.EstimateGb(7, qlora: false, saveMemory: false), 24));
        Assert.Equal(MemoryFit.TooBig, TrainingMemory.Fit(TrainingMemory.EstimateGb(7, qlora: true, saveMemory: false), 6));
        Assert.Equal(MemoryFit.Tight, TrainingMemory.Fit(TrainingMemory.EstimateGb(7, qlora: true, saveMemory: false), 8));
        Assert.Equal(MemoryFit.Fits, TrainingMemory.Fit(TrainingMemory.EstimateGb(7, qlora: true, saveMemory: true), 8));
        Assert.Equal(MemoryFit.TooBig, TrainingMemory.Fit(TrainingMemory.EstimateGb(7, qlora: true, saveMemory: true), 6));
        Assert.True(TrainingMemory.EstimateGb(7, qlora: true, saveMemory: true) < TrainingMemory.EstimateGb(7, qlora: true, saveMemory: false));
    }

    [Theory]
    [InlineData(4, 4.0)]
    [InlineData(6, 4.0)]
    [InlineData(8, 8.0)]
    [InlineData(16, 14.0)]
    [InlineData(48, 32.0)]
    public void The_largest_model_a_GPU_trains_grows_with_its_memory(int gpuMemoryGb, double billions) =>
        Assert.Equal(billions, TrainingMemory.LargestFitting(gpuMemoryGb));

    [Fact]
    public void Smaller_GPUs_take_one_sequence_at_a_time_and_keep_eight_a_step()
    {
        Assert.Equal((2, 4), TrainingMemory.Batch(null));
        Assert.Equal((1, 8), TrainingMemory.Batch(6));
        Assert.Equal((1, 8), TrainingMemory.Batch(12));
        Assert.Equal((2, 4), TrainingMemory.Batch(16));
        Assert.Equal((4, 2), TrainingMemory.Batch(24));
        Assert.Null(TrainingMemory.LargestFitting(1));
    }
}
