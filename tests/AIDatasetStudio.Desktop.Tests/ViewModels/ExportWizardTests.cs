using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Export;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Export;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// The export wizard (M5) over a real workspace.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ExportWizardTests
{
    public ExportWizardTests() => Localizer.Instance.SetCulture(Localizer.English);

    private static RecordPayload[] Samples =>
    [
        new InstructionPayload("Kargo ne zaman gelir?", "", "1-3 iş günü içinde."),
        new InstructionPayload("Kargo ne zaman gelir?", "", "1-3 iş günü içinde."), // copy
        new InstructionPayload("Fatura için kime yazayım?", "", "ali@firma.com adresine."),
        new InstructionPayload("İade süresi?", "", "14 gün."),
        new InstructionPayload("Mesai saatleri?", "", "09.00 – 18.00."),
    ];

    [Fact]
    public Task Exports_from_the_file_menu_and_reports_the_files() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        await ApproveAllAsync(host);
        host.Files.ExportPath = Path.Combine(host.Directory, "faq.jsonl");

        Assert.True(host.Main.ExportCommand.CanExecute(null));
        await host.Main.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Dataset.jsonl", host.Files.SuggestedExportName);
        var lines = await File.ReadAllLinesAsync(host.Files.ExportPath, TestContext.Current.CancellationToken);
        Assert.Equal(4, lines.Length); // the copy is left out
        Assert.StartsWith("""{"messages":[{"role":"user","content":"Kargo ne zaman gelir?"}""", lines[0], StringComparison.Ordinal);
        Assert.Contains("[EMAIL_1]", lines[1], StringComparison.Ordinal);
        Assert.Equal("Exported 4 records of 'Dataset': faq.jsonl", host.Main.Status.Message);
        var wizard = host.Dialogs.LastExportWizard!;
        Assert.True(wizard.IsDone);
        Assert.Equal("Wrote 4 records of 'Dataset'.", wizard.ResultText);
        Assert.Equal([("faq.jsonl", "4 records")], wizard.WrittenFiles.Select(f => (f.Name, f.CountText)));
        Assert.Contains(wizard.SkipItems, s => s.Text == "Left out: copies of an earlier record" && s.CountText == "1");
        Assert.Equal(dataset.Id, wizard.Target.DatasetId);
    });

    [Fact]
    public Task The_preview_follows_the_options() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var wizard = CreateWizard(host, dataset.Id);
        await wizard.InitializeAsync();

        // Nothing is approved yet.
        Assert.Equal(["OpenAI JSONL", "Alpaca", "ShareGPT", "ChatML text", "Llama 3 text"], wizard.Formats.Select(f => f.Label));
        Assert.Equal(0, wizard.Preview!.Selected);
        Assert.True(wizard.ShowNoApprovedHint);
        Assert.False(wizard.ExportCommand.CanExecute(null));

        wizard.IncludeDraft = true;
        await wizard.PendingWork;
        Assert.Equal(4, wizard.Preview!.Exported);
        Assert.Equal("Export 4 records", wizard.StartText);
        Assert.Equal(ExportWizardViewModel.SampleLines, wizard.Samples.Count);
        Assert.Contains(wizard.SkipItems, s => s.CountText == "1" && !s.IsInfo);
        Assert.True(wizard.ExportCommand.CanExecute(null));

        wizard.SelectedFormat = wizard.Formats.Single(f => f.Format == ExportFormat.Alpaca);
        await wizard.PendingWork;
        Assert.Equal("""{"instruction":"Kargo ne zaman gelir?","input":"","output":"1-3 iş günü içinde."}""", wizard.Samples[0]);

        wizard.MaskPersonalData = false;
        await wizard.PendingWork;
        Assert.Contains("ali@firma.com", wizard.Samples[1], StringComparison.Ordinal);

        wizard.SelectedSplitMode = wizard.SplitModes.Single(m => m.Mode == SplitMode.Standard);
        await wizard.PendingWork;
        Assert.Equal("train ≈ 3 · validation ≈ 0 · test ≈ 0", wizard.SplitEstimateText);

        wizard.SelectedSplitMode = wizard.SplitModes.Single(m => m.Mode == SplitMode.Custom);
        wizard.TrainPercent = 50;
        await wizard.PendingWork;
        Assert.Equal("The shares must add up to 100, with some training data.", wizard.ProblemText);
        Assert.False(wizard.ExportCommand.CanExecute(null));

        wizard.TrainPercent = 80;
        wizard.IncludeApproved = false;
        wizard.IncludeDraft = false;
        await wizard.PendingWork;
        Assert.Equal("Choose at least one status.", wizard.ProblemText);
        Assert.Null(wizard.Options);
    });

    [Fact]
    public Task A_split_asks_before_overwriting_files_in_the_folder() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var folder = Path.Combine(host.Directory, "split");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "train.jsonl"), "eski\n", TestContext.Current.CancellationToken);
        host.Files.ExportFolder = folder;
        var wizard = CreateWizard(host, dataset.Id);
        await wizard.InitializeAsync();
        wizard.IncludeDraft = true;
        wizard.SelectedSplitMode = wizard.SplitModes.Single(m => m.Mode == SplitMode.Standard);
        await wizard.PendingWork;

        await wizard.ExportCommand.ExecuteAsync(null);

        Assert.True(wizard.IsConfirmingOverwrite);
        Assert.Equal($"train.jsonl already exist in {folder}. Overwrite them?", wizard.OverwriteText);
        Assert.Equal("eski\n", await File.ReadAllTextAsync(Path.Combine(folder, "train.jsonl"), TestContext.Current.CancellationToken));
        wizard.CancelOverwriteCommand.Execute(null);
        Assert.False(wizard.IsConfirmingOverwrite);

        await wizard.ExportCommand.ExecuteAsync(null);
        await wizard.ConfirmOverwriteCommand.ExecuteAsync(null);

        Assert.True(wizard.IsDone);
        Assert.Equal(["train.jsonl", "val.jsonl", "test.jsonl"], wizard.WrittenFiles.Select(f => f.Name));
        Assert.Equal(4, wizard.Result!.Files.Sum(f => f.Count));
        Assert.DoesNotContain("eski", await File.ReadAllTextAsync(Path.Combine(folder, "train.jsonl"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    });

    [Fact]
    public Task Records_the_model_scored_low_can_be_left_out() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var queries = host.Get<IRecordQueryService>();
        foreach (var row in host.Main.Records.Items)
        {
            if (await queries.GetAsync(row.Id) is { Payload: InstructionPayload { Instruction: "İade süresi?" } } record)
            {
                await host.Get<IJudgmentService>().SaveAsync(new RecordJudgment(record.Id, record.ContentHash, 1, "Eksik.", JudgeFlags.None, "qwen2.5:7b", DateTimeOffset.UtcNow));
            }
        }

        var wizard = CreateWizard(host, dataset.Id);
        await wizard.InitializeAsync();
        wizard.IncludeDraft = true;
        await wizard.PendingWork;

        // Off by default.
        Assert.True(wizard.CanSkipLowJudgeScore);
        Assert.False(wizard.SkipLowJudgeScore);
        Assert.Equal(4, wizard.Preview!.Exported);

        wizard.SkipLowJudgeScore = true;
        await wizard.PendingWork;
        Assert.Equal(3, wizard.Preview!.Exported);
        Assert.Contains(wizard.SkipItems, s => s.Text == "Left out: the model scored them low" && s.CountText == "1");
        Assert.True(wizard.Options!.SkipLowJudgeScore);
    });

    [Fact]
    public Task The_last_settings_are_restored() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var first = CreateWizard(host, dataset.Id);
        await first.InitializeAsync();
        first.SelectedFormat = first.Formats.Single(f => f.Format == ExportFormat.ShareGpt);
        first.IncludeDraft = true;
        first.SkipDuplicates = false;
        first.SelectedSplitMode = first.SplitModes.Single(m => m.Mode == SplitMode.TrainValidation);
        first.Seed = 7;
        await first.PendingWork;
        host.Files.ExportFolder = Path.Combine(host.Directory, "out");
        await first.ExportCommand.ExecuteAsync(null);
        Assert.True(first.IsDone);

        var second = CreateWizard(host, dataset.Id);
        await second.InitializeAsync();

        Assert.Equal(ExportFormat.ShareGpt, second.SelectedFormat!.Format);
        Assert.True(second.IncludeDraft);
        Assert.False(second.SkipDuplicates);
        Assert.Equal(SplitMode.TrainValidation, second.SelectedSplitMode!.Mode);
        Assert.Equal(7, second.Seed);
        Assert.StartsWith("Last export: ", second.LastExportText, StringComparison.Ordinal);
        Assert.EndsWith(" · ShareGPT · 5 records", second.LastExportText, StringComparison.Ordinal);
    });

    [Fact]
    public Task A_training_configuration_is_written_with_the_files_and_its_model_follows_the_format() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        var wizard = CreateWizard(host, dataset.Id);
        await wizard.InitializeAsync();
        wizard.IncludeDraft = true;
        wizard.SelectedSplitMode = wizard.SplitModes.Single(m => m.Mode == SplitMode.TrainValidation);
        Assert.Equal(["None", "Axolotl", "LLaMA-Factory"], wizard.TrainingChoices.Select(c => c.Label));
        Assert.False(wizard.IsTraining);

        // The suggested model follows the format's chat template until the user types one.
        wizard.SelectedTraining = wizard.TrainingChoices.Single(c => c.Framework == TrainingFramework.LlamaFactory);
        Assert.Equal("Qwen/Qwen2.5-7B-Instruct", wizard.BaseModel);
        wizard.SelectedFormat = wizard.Formats.Single(f => f.Format == ExportFormat.Llama3);
        Assert.Equal("meta-llama/Llama-3.1-8B-Instruct", wizard.BaseModel);
        await wizard.PendingWork;
        Assert.Null(wizard.Options);
        Assert.Equal("LLaMA-Factory cannot read Llama 3 text files; choose another format.", wizard.ProblemText);

        wizard.SelectedFormat = wizard.Formats.Single(f => f.Format == ExportFormat.ShareGpt);
        wizard.BaseModel = "ytu-ce-cosmos/Turkish-Llama-8b-Instruct-v0.1";
        await wizard.PendingWork;
        Assert.StartsWith("dataset_info.json and llamafactory.yaml are written next to the files", wizard.TrainingHint, StringComparison.Ordinal);
        var folder = Path.Combine(host.Directory, "egitim");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "llamafactory.yaml"), "eski\n", TestContext.Current.CancellationToken);
        host.Files.ExportFolder = folder;

        // An earlier configuration is not replaced without asking.
        await wizard.ExportCommand.ExecuteAsync(null);
        Assert.True(wizard.IsConfirmingOverwrite);
        await wizard.ConfirmOverwriteCommand.ExecuteAsync(null);

        Assert.Equal(["train.jsonl", "val.jsonl", "dataset_info.json", "llamafactory.yaml"], wizard.WrittenFiles.Select(f => f.Name));
        Assert.Equal(["training configuration", "training configuration"], wizard.WrittenFiles.Skip(2).Select(f => f.CountText));
        var yaml = await File.ReadAllTextAsync(Path.Combine(folder, "llamafactory.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("model_name_or_path: \"ytu-ce-cosmos/Turkish-Llama-8b-Instruct-v0.1\"\n", yaml, StringComparison.Ordinal);
        // A model whose name does not tell its family leaves the template to the user.
        Assert.Contains("template: default  # set your model's template\n", yaml, StringComparison.Ordinal);

        // The tool and the model are remembered with the dataset's other settings.
        var again = CreateWizard(host, dataset.Id);
        await again.InitializeAsync();
        Assert.Equal((TrainingFramework.LlamaFactory, "ytu-ce-cosmos/Turkish-Llama-8b-Instruct-v0.1"), (again.SelectedTraining!.Framework, again.BaseModel));
    });

    [Fact]
    public Task Cancelling_closes_without_writing() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, Samples);
        host.Dialogs.ExportWizardScript = async wizard =>
        {
            await wizard.InitializeAsync();
            var closed = false;
            wizard.CloseRequested += (_, _) => closed = true;
            wizard.CancelCommand.Execute(null);
            Assert.True(closed);
        };

        await host.Main.ExportCommand.ExecuteAsync(null);

        Assert.Null(host.Dialogs.LastExportWizard!.Result);
        Assert.Empty(Directory.EnumerateFiles(host.Directory, "*.jsonl"));
    });

    [Theory]
    [InlineData("Sık Sorulan Sorular", "Sık Sorulan Sorular.jsonl")]
    [InlineData("Soru: cevap?", "Soru_ cevap_.jsonl")]
    [InlineData("a/b\\c", "a_b_c.jsonl")]
    [InlineData("  ...  ", "dataset.jsonl")]
    public void Suggested_file_names_are_safe(string datasetName, string expected)
    {
        Assert.Equal(expected, ExportWizardViewModel.SuggestedFileName(datasetName));
    }

    private static ExportWizardViewModel CreateWizard(DesktopTestHost host, Guid datasetId) =>
        new(host.Get<IExportService>(), host.Files, Localizer.Instance, new ExportTarget(datasetId, "Dataset", DatasetType.Instruction));

    private static async Task ApproveAllAsync(DesktopTestHost host)
    {
        var ids = host.Main.Records.Items.Select(row => row.Id).ToList();
        await host.Get<IRecordCommandService>().SetStatusAsync(ids, RecordStatus.Approved, TestContext.Current.CancellationToken);
    }
}
