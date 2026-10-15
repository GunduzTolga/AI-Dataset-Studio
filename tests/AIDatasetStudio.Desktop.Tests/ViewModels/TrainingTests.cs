using System.Globalization;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Desktop.ViewModels.Training;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Training a model from the app (M28): the window over a training tool that the test scripts.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TrainingTests
{
    public TrainingTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static string N(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    [Fact]
    public Task The_run_shows_its_steps_losses_GPU_and_log_as_it_goes_and_where_the_adapter_went() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var folder = ExportFolder(host);
        host.Gpu.Memory = new GpuMemory("AMD Radeon RX 7800 XT", 6144, 16384);
        host.Trainer.Events.AddRange(
        [
            new TrainingLogLine("  Total optimization steps = 66"),
            new TrainingStep(10, 66, TimeSpan.FromSeconds(75), TimeSpan.FromMinutes(7)),
            new TrainingMetrics(0.47, 1.8123, null, 9.97e-05),
            new TrainingStep(22, 66, TimeSpan.FromMinutes(3), new TimeSpan(1, 2, 0)),
            new TrainingMetrics(1.0, null, 1.6, null),
            new TrainingMetrics(1.4, 1.5, null, 6.9e-05),
        ]);
        using var training = host.Get<Desktop.Services.TrainingLauncher>().Create(new TrainingRunTarget(folder, TrainingFramework.LlamaFactory, "Destek SSS"));

        Assert.Equal("Destek SSS · LLaMA-Factory", training.TitleText);
        Assert.Equal("/opt/venv/bin/llamafactory-cli", training.ToolPath);
        Assert.Equal("llamafactory-cli train llamafactory.yaml", training.CommandText);
        Assert.Equal(("Ready", "Not started"), (training.StateText, training.StepText));
        Assert.True(training.StartCommand.CanExecute(null));

        var run = training.StartCommand.ExecuteAsync(null);
        await host.Trainer.Started.Task;
        await UiTest.UntilAsync(() => training.EvalPoints.Count == 1 && training.TrainPoints.Count == 2 && training.HasGpu);

        Assert.True(training.IsRunning);
        Assert.False(training.StartCommand.CanExecute(null));
        Assert.Equal($"Step 22 / 66 · {N(100.0 * 22 / 66, "0")}%", training.StepText);
        Assert.Equal("3 min 0 s elapsed · about 1 h 2 min left", training.TimeText);
        Assert.Equal((N(1.4, "0.##"), N(1.5, "0.0000"), N(1.6, "0.0000")), (training.EpochText, training.LossText, training.EvalLossText));
        Assert.Equal([new ChartPoint(0.47, 1.8123), new ChartPoint(1.4, 1.5)], training.TrainPoints);
        Assert.Equal($"AMD Radeon RX 7800 XT\n{N(6.0, "0.0")} / {N(16.0, "0.0")} GB in use", training.GpuText?.ReplaceLineEndings("\n"));
        Assert.Equal("  Total optimization steps = 66", training.LogText);
        Assert.Equal(new TrainingRequest(TrainingFramework.LlamaFactory, folder, "/opt/venv/bin/llamafactory-cli"), Assert.Single(host.Trainer.Requests));

        host.Trainer.Finish.SetResult();
        await run;

        Assert.Equal((TrainingState.Succeeded, "Finished"), (training.State, training.StateText));
        Assert.Equal("Training finished. The LoRA adapter is in /data/saves/lora/sft.", training.ResultText);
        Assert.Equal("Took 3 min 0 s", training.TimeText);
        Assert.False(training.OpenOutputCommand.CanExecute(null)); // that folder is not on this computer
    });

    [Fact]
    public Task Without_the_tool_the_window_says_how_to_install_it_and_the_chosen_command_is_remembered() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var folder = ExportFolder(host);
        host.Trainer.Tool = null;
        using var training = host.Get<Desktop.Services.TrainingLauncher>().Create(new TrainingRunTarget(folder, TrainingFramework.LlamaFactory, "Destek SSS"));

        Assert.False(training.HasTool);
        Assert.False(training.StartCommand.CanExecute(null));
        Assert.StartsWith("LLaMA-Factory was not found on this computer. Install it in Python (pip install llamafactory)", training.ToolHint, StringComparison.Ordinal);

        host.Files.TrainingTool = "/home/me/venv/bin/llamafactory-cli";
        await training.BrowseToolCommand.ExecuteAsync(null);
        Assert.True(training.StartCommand.CanExecute(null));
        var run = training.StartCommand.ExecuteAsync(null);
        await host.Trainer.Started.Task;
        host.Trainer.Finish.SetResult();
        await run;

        Assert.Equal("/home/me/venv/bin/llamafactory-cli", host.Get<ITrainingToolSettings>().GetToolPath(TrainingFramework.LlamaFactory));
    });

    [Fact]
    public Task Closing_while_training_asks_first_and_stopping_keeps_what_was_saved() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        using var training = host.Get<Desktop.Services.TrainingLauncher>().Create(new TrainingRunTarget(ExportFolder(host), TrainingFramework.LlamaFactory, "Destek SSS"));
        var run = training.StartCommand.ExecuteAsync(null);
        await host.Trainer.Started.Task;

        Assert.False(training.TryClose());
        Assert.True(training.IsConfirmingClose);
        training.KeepRunningCommand.Execute(null);
        Assert.False(training.IsConfirmingClose);
        Assert.True(training.IsRunning);

        var closed = false;
        training.CloseRequested += (_, _) => closed = true;
        Assert.False(training.TryClose());
        await training.StopAndCloseCommand.ExecuteAsync(null);
        await run;

        Assert.True(closed);
        Assert.Equal(TrainingState.Stopped, training.State);
        Assert.Equal("Training was stopped. The checkpoints saved so far are in the output folder.", training.ResultText);
        Assert.True(training.TryClose());
    });

    [Fact]
    public Task A_run_out_of_GPU_memory_says_what_to_change() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var output = Path.Combine(host.Directory, "saves");
        Directory.CreateDirectory(output);
        host.Trainer.Result = new TrainingResult(1, Cancelled: false, output, TrainingFailure.OutOfMemory, ["torch.OutOfMemoryError: CUDA out of memory."]);
        using var training = host.Get<Desktop.Services.TrainingLauncher>().Create(new TrainingRunTarget(ExportFolder(host), TrainingFramework.LlamaFactory, "Destek SSS"));
        var run = training.StartCommand.ExecuteAsync(null);
        await host.Trainer.Started.Task;
        host.Trainer.Finish.SetResult();
        await run;

        Assert.Equal((TrainingState.Failed, "Failed"), (training.State, training.StateText));
        Assert.StartsWith("The GPU ran out of memory. Export again with QLoRA and memory saving turned on", training.ResultText, StringComparison.Ordinal);

        // What was saved before it failed can be looked at.
        await training.OpenOutputCommand.ExecuteAsync(null);
        Assert.Equal([output], host.Shell.Opened);
        Assert.True(training.StartCommand.CanExecute(null));
    });

    [Fact]
    public Task Training_starts_from_the_export_result_and_from_the_tools_menu() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, [.. Enumerable.Range(1, 12).Select(i => (RecordPayload)new InstructionPayload($"Soru {i}", "", $"Cevap {i}"))]);
        var folder = Path.Combine(host.Directory, "egitim");
        Directory.CreateDirectory(folder);
        host.Files.ExportFolder = folder;
        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            wizard.IncludeDraft = true;
            wizard.SelectedSplitMode = wizard.SplitModes.Single(m => m.Mode == SplitMode.TrainValidation);
            wizard.SelectedTraining = wizard.TrainingChoices.Single(c => c.Framework == TrainingFramework.LlamaFactory);
            await wizard.PendingWork;
            await wizard.ExportCommand.ExecuteAsync(null);
            Assert.True(wizard.CanTrain);
            wizard.StartTrainingCommand.Execute(null);
        };
        host.Dialogs.TrainingScript = async training =>
        {
            var run = training.StartCommand.ExecuteAsync(null);
            await host.Trainer.Started.Task;
            host.Trainer.Finish.SetResult();
            await run;
        };

        await host.Main.ExportCommand.ExecuteAsync(null);

        Assert.Equal(new TrainingRunTarget(folder, TrainingFramework.LlamaFactory, "Dataset", dataset.Id, DatasetType.Instruction), host.Dialogs.LastTraining!.Target);
        Assert.Equal("Training of Dataset finished: /data/saves/lora/sft", host.Main.Status.Message);

        // Tools › Train a Model: a folder exported before, or one without a configuration.
        host.Files.TrainingFolder = folder;
        await host.Main.TrainModelCommand.ExecuteAsync(null);
        Assert.Equal(new TrainingRunTarget(folder, TrainingFramework.LlamaFactory, "egitim"), host.Dialogs.LastTraining!.Target);

        host.Files.TrainingFolder = host.Directory;
        var last = host.Dialogs.LastTraining;
        await host.Main.TrainModelCommand.ExecuteAsync(null);
        Assert.Same(last, host.Dialogs.LastTraining);
        Assert.StartsWith(Path.GetFileName(host.Directory) + " has no training configuration", host.Main.Status.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task After_training_from_the_export_the_trained_model_is_evaluated_on_the_dataset() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [.. Enumerable.Range(1, 30).Select(i => (RecordPayload)new InstructionPayload($"Soru {i}", "", "14 gün içinde."))]);
        host.AddAIConnection();
        var folder = Path.Combine(host.Directory, "egitim");
        Directory.CreateDirectory(folder);
        host.Files.ExportFolder = folder;
        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            wizard.IncludeDraft = true;
            wizard.SelectedSplitMode = wizard.SplitModes.Single(m => m.Mode == SplitMode.Standard);
            wizard.SelectedTraining = wizard.TrainingChoices.Single(c => c.Framework == TrainingFramework.LlamaFactory);
            await wizard.PendingWork;
            await wizard.ExportCommand.ExecuteAsync(null);
            wizard.StartTrainingCommand.Execute(null);
        };
        host.Dialogs.TrainingScript = async training =>
        {
            Assert.False(training.CanEvaluateModel);
            var run = training.StartCommand.ExecuteAsync(null);
            await host.Trainer.Started.Task;

            // The tool saves the adapter where the configuration says.
            var adapter = Path.Combine(folder, "saves", "dataset", "lora", "sft");
            Directory.CreateDirectory(adapter);
            await File.WriteAllTextAsync(Path.Combine(adapter, TrainedAdapters.AdapterFile), "{}");
            host.Trainer.Finish.SetResult();
            await run;

            Assert.True(training.CanEvaluateModel);
            var closed = false;
            training.CloseRequested += (_, _) => closed = true;
            training.EvaluateCommand.Execute(null);
            Assert.True(closed);
        };
        host.AI.Answers.Enqueue(["14 gün içinde."]);
        host.AI.Answers.Enqueue(["""{"score": 5, "reason": "Aynı."}"""]);
        host.Dialogs.EvaluationScript = async evaluation =>
        {
            // The adapter just trained is the model; the connection grades it.
            Assert.Equal("Trained model: Qwen2.5-7B-Instruct + LoRA (egitim)", evaluation.SelectedModel!.Label);
            Assert.Equal(["qwen2.5:7b · Ollama"], evaluation.Judges.Select(j => j.Label));
            Assert.Equal("qwen2.5:7b · Ollama", evaluation.SelectedJudge!.Label);
            evaluation.SampleSize = 1;

            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;

            Assert.Null(evaluation.ErrorMessage);
            Assert.Equal("Qwen2.5-7B-Instruct + LoRA (egitim)", Assert.Single(evaluation.History).Model);
            evaluation.Dispose();
        };

        await host.Main.ExportCommand.ExecuteAsync(null);

        // LLaMA-Factory serves the adapter in the export's folder; the trained model answers, the connection grades.
        var (served, tool) = Assert.Single(host.Server.Starts);
        Assert.Equal((folder, "Qwen/Qwen2.5-7B-Instruct", "saves/dataset/lora/sft", "/opt/venv/bin/llamafactory-cli"), (served.Folder, served.BaseModel, served.AdapterDirectory, tool));
        var requests = host.AI.Requests.ToList();
        Assert.Equal(host.Server.Models[0].Connection, requests[0].Connection);
        Assert.Equal("qwen2.5:7b", requests[1].Connection.Model);

        // Closing the window stops the server.
        Assert.True(host.Server.Models[0].Disposed);
    });

    private static string ExportFolder(DesktopTestHost host)
    {
        var folder = Path.Combine(host.Directory, "egitim");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TrainingConfigs.LlamaFactoryFile), "output_dir: \"saves\"\n");
        return folder;
    }
}
