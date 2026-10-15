using AIDatasetStudio.Application.Training;

namespace AIDatasetStudio.Application.Tests.Training;

/// <summary>
/// Reading a training tool's output (M28), against what LLaMA-Factory 0.9.5 really printed.
/// </summary>
public sealed class TrainingOutputReaderTests
{
    private static string Output => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Training", "llamafactory-sft-output.txt"));

    [Fact]
    public void Steps_and_losses_are_read_from_a_LLaMA_Factory_run_whatever_size_the_pieces_arrive_in()
    {
        foreach (var size in new[] { 1, 7, 4096, int.MaxValue })
        {
            var reader = new TrainingOutputReader();
            var events = new List<TrainingEvent>();
            var output = Output;
            for (var at = 0; at < output.Length; at += Math.Min(size, output.Length - at))
            {
                events.AddRange(reader.Read(output.AsSpan(at, Math.Min(size, output.Length - at))));
            }

            events.AddRange(reader.Finish());

            // Only the training bar counts: not the data preparation's (169 examples) nor the evaluations' (34).
            var steps = events.OfType<TrainingStep>().ToList();
            Assert.All(steps, s => Assert.Equal(66, s.TotalSteps));
            Assert.Equal(66, steps[^1].Step);
            Assert.Equal(steps.Select(s => s.Step).Order(), steps.Select(s => s.Step));
            Assert.Contains(steps, s => s.Remaining == TimeSpan.FromSeconds(3));

            var metrics = events.OfType<TrainingMetrics>().ToList();
            var losses = metrics.Where(m => m.Loss is not null && !m.IsFinal).ToList();
            Assert.Equal([6.392, 6.383, 6.362, 6.333, 6.323, 6.318], losses.Select(m => m.Loss!.Value));
            Assert.Equal((0.4706, 9.972e-05), (losses[0].Epoch!.Value, losses[0].LearningRate!.Value));
            Assert.Equal([6.371, 6.34, 6.334], metrics.Where(m => m.EvalLoss is not null).Select(m => m.EvalLoss!.Value));
            var final = Assert.Single(metrics, m => m.IsFinal);
            Assert.Equal(6.349, final.Loss!.Value, 3);

            // The log gets whole lines, not each redraw of a bar.
            Assert.Contains(events.OfType<TrainingLogLine>(), l => l.Text.Contains("Total optimization steps = 66", StringComparison.Ordinal));
            Assert.Contains(reader.LastLines, l => l.Trim() == "eval_loss               =     6.3336");
        }
    }

    [Fact]
    public void Windows_line_endings_end_lines_and_numbers_printed_unquoted_are_read()
    {
        var reader = new TrainingOutputReader(keepLines: 2);
        var events = reader.Read("  Total optimization steps = 1,200\r\n 1%|  | 12/1200 [01:02<1:42:03, 3.2s/it]\r 2%|  | 24/1200 [02:04<1:40:00, 3.2s/it]\r\n{'loss': 1.25, 'learning_rate': 2e-05, 'epoch': 0.06}\r\nlast\r\n");

        Assert.Equal(
            [new TrainingStep(12, 1200, TimeSpan.FromSeconds(62), new TimeSpan(1, 42, 3)), new TrainingStep(24, 1200, TimeSpan.FromSeconds(124), new TimeSpan(1, 40, 0))],
            events.OfType<TrainingStep>());
        Assert.Equal(new TrainingMetrics(0.06, 1.25, null, 2e-05), Assert.Single(events.OfType<TrainingMetrics>()));
        Assert.Equal(["{'loss': 1.25, 'learning_rate': 2e-05, 'epoch': 0.06}", "last"], reader.LastLines);
    }

    [Fact]
    public void Lines_from_standard_output_and_standard_error_stay_whole_however_they_interleave()
    {
        // tqdm redraws its bar on stderr while the trainer's dictionary is half written on stdout.
        var reader = new TrainingOutputReader();
        var events = new List<TrainingEvent>();
        events.AddRange(reader.Read("  Total optimization steps = 66\n", TrainingOutputStream.StandardOutput));
        events.AddRange(reader.Read("\r 15%|█▌        | 10/66 [00:00<00:03, 16.81it/s]", TrainingOutputStream.StandardError));
        events.AddRange(reader.Read("{'loss': '6.392', 'grad_norm': '0.4496', ", TrainingOutputStream.StandardOutput));
        events.AddRange(reader.Read("\r 17%|█▋        | 11/66 [00:00<00:03, 16.80it/s]", TrainingOutputStream.StandardError));
        events.AddRange(reader.Read("'learning_rate': '9.972e-05', 'epoch': '0.4706'}\n", TrainingOutputStream.StandardOutput));
        events.AddRange(reader.Read("\r 18%|█▊        | 12/66 [00:00<00:03, 16.79it/s]\n", TrainingOutputStream.StandardError));
        events.AddRange(reader.Finish());

        Assert.Equal([10, 11, 12], events.OfType<TrainingStep>().Select(s => s.Step));
        Assert.Equal(new TrainingMetrics(0.4706, 6.392, null, 9.972e-05), Assert.Single(events.OfType<TrainingMetrics>()));
        Assert.Equal(
            [
                "  Total optimization steps = 66",
                "{'loss': '6.392', 'grad_norm': '0.4496', 'learning_rate': '9.972e-05', 'epoch': '0.4706'}",
                " 18%|█▊        | 12/66 [00:00<00:03, 16.79it/s]",
            ],
            reader.LastLines);
    }

    [Theory]
    [InlineData("torch.OutOfMemoryError: CUDA out of memory. Tried to allocate 2.00 GiB", TrainingFailure.OutOfMemory)]
    [InlineData("RuntimeError: HIP out of memory.", TrainingFailure.OutOfMemory)]
    [InlineData("ModuleNotFoundError: No module named 'liger_kernel'", TrainingFailure.MissingPackage)]
    [InlineData("ValueError: Your setup doesn't support bf16/gpu. You need to assign use_cpu if you want to train the model on CPU.", TrainingFailure.NoGpu)]
    [InlineData("OSError: meta-llama/Llama-3.1-8B is not a local folder and is not a valid model identifier", TrainingFailure.ModelUnavailable)]
    [InlineData("huggingface_hub.errors.GatedRepoError: 401 Client Error.", TrainingFailure.ModelUnavailable)]
    [InlineData("datasets.exceptions.DatasetGenerationError: An error occurred while generating the dataset", TrainingFailure.DatasetError)]
    [InlineData("Segmentation fault", TrainingFailure.Unknown)]
    public void A_failure_is_named_from_the_output(string line, TrainingFailure failure) =>
        Assert.Equal(failure, TrainingOutputReader.Classify(["Traceback (most recent call last):", line]));
}
