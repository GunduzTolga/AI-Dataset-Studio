using System.Text.Json.Nodes;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Training;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.AI;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Tools › Evaluate a Model (M22): a model answers the dataset's questions and a grader compares.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ModelEvaluationTests
{
    public ModelEvaluationTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task A_model_answers_a_sample_and_the_grader_scores_it_against_the_dataset() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            // The same answer to both, so the sample's order (a seeded hash of new ids) does not matter.
            new InstructionPayload("İade süresi?", "", "14 gün içinde."),
            new InstructionPayload("Değişim süresi?", "", "14 gün içinde."),
            new InstructionPayload("Taslak soru", "", "Henüz onaylanmadı."),
        ]);
        var records = host.Main.Records.Items;
        await host.Get<IRecordCommandService>().SetStatusAsync([records[0].Id, records[1].Id], RecordStatus.Approved);
        host.AddAIConnection();
        host.AI.Answers.Enqueue(["14 gün ", "içinde."]);
        host.AI.Answers.Enqueue(["""{"score": 4, "reason": "Doğru, başlangıcı eksik."}"""]);
        host.AI.Answers.Enqueue(["Bilmiyorum."]);
        host.AI.Answers.Enqueue(["""{"score": 1, "reason": "Yanıt yok."}"""]);
        var path = Path.Combine(host.Directory, "rapor.jsonl");
        host.Files.ExportPath = path;

        host.Dialogs.EvaluationScript = async evaluation =>
        {
            Assert.Equal(["qwen2.5:7b · Ollama"], evaluation.Models.Select(m => m.Label));
            Assert.Same(evaluation.SelectedModel, evaluation.SelectedJudge);
            Assert.False(evaluation.ExportCommand.CanExecute(null));

            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;

            // Only approved records are asked; the draft is not.
            Assert.Equal(2, evaluation.Rows.Count);
            Assert.DoesNotContain(evaluation.Rows, r => r.Question == "Taslak soru");
            Assert.Equal("Average 2.5 / 5 · word overlap 0.50 · 2 of 2 scored", evaluation.SummaryText.Replace(',', '.'));
            Assert.Equal("5/5: 0 · 4/5: 1 · 3/5: 0 · 2/5: 0 · 1/5: 1", evaluation.DistributionText);
            Assert.Equal("2 questions evaluated.", evaluation.ProgressText);
            var first = evaluation.Rows[0];
            Assert.Equal(("14 gün içinde.", "4/5", "Doğru, başlangıcı eksik."), (first.Answer, first.ScoreText, first.State));
            Assert.True(first.IsGood);
            Assert.True(evaluation.Rows[1].IsLow);

            // Finished: kept with the dataset to compare models.
            var run = Assert.Single(evaluation.History);
            Assert.Equal(("qwen2.5:7b", "2.5 / 5", "F1 0.50"), (run.Model, run.ScoreText.Replace(',', '.'), run.F1Text.Replace(',', '.')));
            Assert.StartsWith("graded by qwen2.5:7b · all records, seed 42 · 2 questions · ", run.DetailText, StringComparison.Ordinal);
            Assert.Equal("Earlier evaluations (1)", evaluation.HistoryText);

            // The report names the models that answered and graded, whatever is chosen now.
            evaluation.SelectedModel = null;
            evaluation.SelectedJudge = null;
            await evaluation.ExportCommand.ExecuteAsync(null);
        };

        await host.Main.EvaluateModelCommand.ExecuteAsync(null);

        // The question goes as it is, at temperature 0; the grader gets the reference.
        var asked = host.Dialogs.LastEvaluation!.Rows[0].Question;
        var requests = host.AI.Requests.ToList();
        Assert.Equal(4, requests.Count);
        Assert.Equal([(ChatRole.User, asked)], requests[0].Request.Messages.Select(m => (m.Role, m.Content)));
        Assert.Equal(0, requests[0].Request.Temperature);
        Assert.StartsWith("You grade a model's answer", requests[1].Request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("Reference answer:\n14 gün içinde.", requests[1].Request.Messages[1].Content, StringComparison.Ordinal);

        var lines = (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(("qwen2.5:7b", "qwen2.5:7b", asked, "14 gün içinde.", 4), ((string?)lines[0]["model"], (string?)lines[0]["judge"], (string?)lines[0]["question"], (string?)lines[0]["answer"], (int?)lines[0]["score"]));
        Assert.Equal("Dataset - qwen2.5_7b.jsonl", host.Files.SuggestedExportName);
        Assert.Equal((1.0, 0.0), ((double?)lines[0]["f1"], (double?)lines[1]["f1"]));

        // The next time, the evaluation is there; deleting it asks first.
        host.Dialogs.EvaluationScript = async evaluation =>
        {
            var run = Assert.Single(evaluation.History);
            host.Dialogs.ConfirmAnswer = false;
            await evaluation.DeleteRunCommand.ExecuteAsync(run);
            Assert.Single(evaluation.History);

            host.Dialogs.ConfirmAnswer = true;
            await evaluation.DeleteRunCommand.ExecuteAsync(run);
            Assert.False(evaluation.HasHistory);
        };

        await host.Main.EvaluateModelCommand.ExecuteAsync(null);

        Assert.StartsWith("Delete the evaluation of qwen2.5:7b from ", host.Dialogs.ConfirmMessages[^1], StringComparison.Ordinal);
        Assert.Empty(await host.Get<IEvaluationRunService>().GetRunsAsync(dataset.Id, TestContext.Current.CancellationToken));
    });

    [Fact]
    public Task After_a_split_export_the_questions_come_from_its_test_part() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
            [.. Enumerable.Range(0, 40).Select(i => new InstructionPayload($"Soru {Code(i)}", "", "14 gün içinde."))]);
        var options = new ExportOptions(ExportFormat.Alpaca, [RecordStatus.Draft], Split: SplitOptions.Standard);
        var result = await host.Get<IExportService>().ExportAsync(dataset.Id, options, Path.Combine(host.Directory, "split"), cancellationToken: TestContext.Current.CancellationToken);
        var test = File.ReadLines(result.Files.Single(f => f.Part == SplitPart.Test).Path).Select(line => (string?)JsonNode.Parse(line)!["instruction"]).ToList();
        Assert.NotEmpty(test);
        host.AddAIConnection();

        host.Dialogs.EvaluationScript = async evaluation =>
        {
            Assert.Equal(
                ["Test part of the last export (10%, seed 42)", "Validation part of the last export (10%, seed 42)", "All records"],
                evaluation.Sources.Select(s => s.Label));
            Assert.Equal(EvaluationSource.TestPart, evaluation.SelectedSource!.Source);
            Assert.StartsWith("Questions the model was not trained on", evaluation.SourceHint, StringComparison.Ordinal);

            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;

            // The test file's questions only: none the model was trained on.
            Assert.Equal(test.Order(), evaluation.Rows.Select(r => r.Question).Order());
            Assert.Equal(EvaluationSource.TestPart, Assert.Single(evaluation.History).Run.Source);

            evaluation.SelectedSource = evaluation.Sources[^1];
            Assert.True(evaluation.IsAllRecords);
            Assert.StartsWith("A model trained on this dataset has seen most of these questions", evaluation.SourceHint, StringComparison.Ordinal);
        };

        await host.Main.EvaluateModelCommand.ExecuteAsync(null);
        Assert.NotNull(host.Dialogs.LastEvaluation);
    });

    [Fact]
    public Task A_model_trained_on_the_last_export_is_served_for_the_evaluation_and_a_failure_to_load_it_says_why() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
            [.. Enumerable.Range(0, 40).Select(i => new InstructionPayload($"Soru {Code(i)}", "", "14 gün içinde."))]);
        var training = new TrainingOptions(TrainingFramework.LlamaFactory, "Qwen/Qwen2.5-7B-Instruct", QLoRA: true);
        var options = new ExportOptions(ExportFormat.Alpaca, [RecordStatus.Draft], Split: SplitOptions.Standard, Training: training);
        var folder = Path.Combine(host.Directory, "split");
        await host.Get<IExportService>().ExportAsync(dataset.Id, options, folder, cancellationToken: TestContext.Current.CancellationToken);
        host.AddAIConnection();

        // Not trained yet: only the connections.
        host.Dialogs.EvaluationScript = evaluation =>
        {
            Assert.Equal(["qwen2.5:7b · Ollama"], evaluation.Models.Select(m => m.Label));
            return Task.CompletedTask;
        };
        await host.Main.EvaluateModelCommand.ExecuteAsync(null);

        var adapter = Path.Combine(folder, "saves", "dataset", "qlora", "sft");
        Directory.CreateDirectory(adapter);
        await File.WriteAllTextAsync(Path.Combine(adapter, TrainedAdapters.AdapterFile), "{}", TestContext.Current.CancellationToken);
        host.Dialogs.EvaluationScript = async evaluation =>
        {
            // Offered first with the base model before training, but the default connection stays chosen;
            // neither grades.
            Assert.Equal(
                ["Trained model: Qwen2.5-7B-Instruct + QLoRA (split)", "Before training: Qwen2.5-7B-Instruct (4 bit)", "qwen2.5:7b · Ollama"],
                evaluation.Models.Select(m => m.Label));
            Assert.Equal(["qwen2.5:7b · Ollama"], evaluation.Judges.Select(m => m.Label));
            Assert.Same(evaluation.Judges[0], evaluation.SelectedModel);
            evaluation.SelectedModel = evaluation.Models[0];
            evaluation.SampleSize = 1;

            host.Server.Failure = new ModelServerException(TrainingFailure.OutOfMemory, ["torch.OutOfMemoryError: CUDA out of memory."], "stopped");
            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;
            Assert.StartsWith("The trained model could not be loaded: The GPU ran out of memory.", evaluation.ErrorMessage, StringComparison.Ordinal);
            Assert.Empty(evaluation.Rows);
            Assert.False(evaluation.HasHistory);

            host.Trainer.Tool = null;
            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;
            Assert.StartsWith("The trained model is served by LLaMA-Factory, but its command was not found.", evaluation.ErrorMessage, StringComparison.Ordinal);
            host.Trainer.Tool = "/opt/venv/bin/llamafactory-cli";

            // Served once, then asked again without loading it again.
            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;
            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;
            Assert.Null(evaluation.ErrorMessage);
            Assert.Equal(2, host.Server.Starts.Count);
            Assert.Equal(["Qwen2.5-7B-Instruct + QLoRA (split)", "Qwen2.5-7B-Instruct + QLoRA (split)"], evaluation.History.Select(r => r.Model));

            // A server that stopped is started again.
            host.Server.Models[0].HasExited = true;
            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;
            Assert.Equal(3, host.Server.Starts.Count);
            Assert.True(host.Server.Models[0].Disposed);
            Assert.False(host.Server.Models[1].Disposed);

            // The base model on the same questions, for the scores before and after training.
            evaluation.SelectedModel = evaluation.Models[1];
            await evaluation.RunCommand.ExecuteAsync(null);
            await evaluation.PendingWork;
            Assert.True(host.Server.Models[1].Disposed);
            Assert.Equal("Qwen2.5-7B-Instruct (4 bit)", evaluation.History[0].Model);
            evaluation.Dispose();
            Assert.True(host.Server.Models[2].Disposed);
        };

        await host.Main.EvaluateModelCommand.ExecuteAsync(null);
        Assert.All(host.Server.Starts, s => Assert.Equal((folder, true), (s.Adapter.Folder, s.Adapter.IsQLoRA)));
        Assert.Equal([false, false, false, true], host.Server.Starts.Select(s => s.Adapter.BaseOnly));
    });

    // Distinct letters for each question, so no two are near-duplicates.
    private static string Code(int i) => new(new Random(i).GetItems("abcçdefgğhıijklmnoöprsştuüvyz".ToCharArray(), 8));
}
