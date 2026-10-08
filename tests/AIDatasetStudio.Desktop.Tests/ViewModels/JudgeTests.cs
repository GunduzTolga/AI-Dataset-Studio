using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Desktop.Tests.ViewModels;

/// <summary>
/// Record › Judge with AI and the judge window (M15, SDD §17.7).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class JudgeTests
{
    private static RecordPayload[] Kargo =>
    [
        new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri siparişlerde ücretsiz."),
        new InstructionPayload("kargo ücreti ne kadar", "", "Her siparişte 49,90 TL."),
        new InstructionPayload("İade süresi nedir?", "", "14 gün."),
    ];

    public JudgeTests() => Localizer.Instance.SetCulture(Localizer.English);

    // Numbers follow the format culture, as everywhere in the app.
    private static string Decimal(double value) => value.ToString("0.0", CultureInfo.CurrentCulture);

    [Fact]
    public Task The_shown_record_is_judged_in_the_editor_and_compared_with_the_same_question() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, Kargo);
        dataset.AIGuidance = "Resmî yaz.";
        host.AddAIConnection();
        await host.AnalyzeAsync();
        var first = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = first;
        await host.WaitForDetailsAsync(first.Id);
        var details = host.Main.Details;
        Assert.True(details.CanJudgeRecords);
        Assert.False(details.HasJudgment);
        Assert.Equal("Judge with AI", details.JudgeActionText);

        host.AI.Answers.Enqueue(["```json\n{\"score\": 2, ", "\"reason\": \"Sınır doğru ama ücretli siparişler eksik.\"}\n```"]);
        host.AI.Answers.Enqueue(["{\"contradicts\": true, \"reason\": \"Biri ücretsiz, öteki her siparişte 49,90 TL diyor.\"}"]);
        await host.Main.JudgeCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => first.JudgeScore == 2 && host.Main.Overview.Judgments is { JudgedCount: 1 });

        var requests = host.AI.Requests.Select(r => r.Request).ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal(0, r.Temperature));
        Assert.Contains("Write the reason in English", requests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("Instructions for this dataset, which the answer should follow:\nResmî yaz.", requests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.StartsWith("Example A\nQuestion:\nKargo ücreti ne kadar?", requests[1].Messages[1].Content, StringComparison.Ordinal);

        Assert.Equal(("2/5", "Sınır doğru ama ücretli siparişler eksik."), (details.JudgeScoreText, details.JudgeReason));
        Assert.True(details.IsJudgePoor);
        Assert.Equal(["Contradicts #2: Biri ücretsiz, öteki her siparişte 49,90 TL diyor."], details.JudgeNotes);
        Assert.Equal("Judge again", details.JudgeActionText);
        Assert.StartsWith("qwen2.5:7b · ", details.JudgeInfo, StringComparison.Ordinal);
        Assert.Equal("Judged #1 with qwen2.5:7b: 2/5", host.Main.Status.Message);
        Assert.Equal("Score from the AI judge: 2/5", first.JudgeTip);
        Assert.Equal((1L, 1L, 2L), (host.Main.Overview.Judgments!.JudgedCount, host.Main.Overview.Judgments.LowCount, host.Main.Overview.Judgments.ContradictionCount));

        // The record it contradicts shows it too, and the filters find both.
        host.Main.Records.SelectedItem = host.Main.Records.Items[1];
        await host.WaitForDetailsAsync(host.Main.Records.Items[1].Id);
        Assert.False(details.HasJudgment);
        Assert.Equal(["Contradicts #1: Biri ücretsiz, öteki her siparişte 49,90 TL diyor."], details.JudgeNotes);
        host.Main.Records.ShowQuality(QualityFilter.Contradictions);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.Records.IsLoading && host.Main.Records.Items.Count == 2);
        host.Main.Records.ShowQuality(QualityFilter.LowJudgeScore);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.Records.IsLoading && host.Main.Records.Items.Count == 1);

        // An edit makes the judgment out of date; judging again replaces it (no new comparison).
        host.Main.Records.ShowQuality(QualityFilter.None);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.Records.IsLoading && host.Main.Records.Items.Count == 3);
        host.Main.Records.SelectedItem = host.Main.Records.Items[0];
        await host.WaitForDetailsAsync(first.Id);
        details.Fields[2].Text = "500 TL üzeri ücretsiz, altında 49,90 TL.";
        await details.SaveCommand.ExecuteAsync(null);
        Assert.True(details.IsJudgmentOutdated);
        await host.AnalyzeAsync();
        host.AI.Answers.Enqueue(["{\"score\": 5, \"reason\": \"Eksiksiz.\"}"]);
        host.AI.Answers.Enqueue(["{\"contradicts\": false, \"reason\": \"Aynı bilgi.\"}"]);
        await details.JudgeCommand.ExecuteAsync(null);
        Assert.Equal(("5/5", false), (details.JudgeScoreText, details.IsJudgmentOutdated));
        Assert.Empty(details.JudgeNotes);
        Assert.Equal(4, host.AI.Requests.Count);
    });

    [Fact]
    public Task Judging_can_be_stopped_and_explains_what_keeps_it_from_starting() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Preference,
        [
            new PreferencePayload("İade süresi nedir?", "14 gün.", "Bilmiyorum."),
            new PreferencePayload("Fatura nerede?", "", ""),
        ]);
        host.Main.Records.SelectedItem = host.Main.Records.Items[0];
        await host.WaitForDetailsAsync(host.Main.Records.Items[0].Id);
        var details = host.Main.Details;

        await details.JudgeCommand.ExecuteAsync(null);
        Assert.Equal("Set up an AI connection to write with a model: Ollama or LM Studio on this computer is enough.", details.AIError);
        host.AddAIConnection();

        // Stopping keeps the record as it was.
        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["{\"score\": ", "3}"]);
        var running = details.JudgeCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => details.IsJudging);
        Assert.Equal("Judging with qwen2.5:7b…", details.JudgingText);
        Assert.True(host.Main.JudgeCommand.CanExecute(null));
        await host.Main.JudgeCommand.ExecuteAsync(null); // Mod+J again stops
        await running;
        Assert.False(details.IsJudging);
        Assert.False(details.HasJudgment);
        Assert.Equal("Judging stopped; what was judged is kept", host.Main.Status.Message);
        host.AI.Hold = null;

        // The preference order and an answer without a score.
        host.AI.Answers.Enqueue(["{\"score\": 3, \"reason\": \"Reddedilen daha açık.\", \"better\": \"rejected\"}"]);
        await details.JudgeCommand.ExecuteAsync(null);
        Assert.Equal(["The rejected answer looks better than the chosen one."], details.JudgeNotes);
        Assert.Contains("\"better\"", host.AI.Requests.Last().Request.Messages[0].Content, StringComparison.Ordinal);
        host.AI.Answers.Enqueue(["Bu yanıt fena değil."]);
        await details.JudgeCommand.ExecuteAsync(null);
        Assert.Equal("The model's answer had no score from 1 to 5. Judge again or try another model.", details.AIError);
        Assert.Equal("3/5", details.JudgeScoreText);

        host.Main.Records.SelectedItem = host.Main.Records.Items[1];
        await host.WaitForDetailsAsync(host.Main.Records.Items[1].Id);
        await details.JudgeCommand.ExecuteAsync(null);
        Assert.Equal("There is nothing to judge yet: the question or the answer is empty.", details.AIError);
    });

    [Fact]
    public Task Selected_records_are_judged_in_the_window_and_the_overview_offers_the_rest() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [.. Kargo, new InstructionPayload("Boş yanıt", "", "")]);
        host.AddAIConnection();
        await host.AnalyzeAsync();
        Assert.False(host.Main.Overview.HasJudgments);
        Assert.Equal("Judge 4 records…", host.Main.Overview.JudgeRestText);

        Assert.True(host.Main.Records.Select([.. host.Main.Records.Items.Take(3).Select(r => r.Id)]));
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Details.IsBatch);
        host.AI.Answers.Enqueue(["{\"score\": 4, \"reason\": \"İyi.\"}"]);
        host.AI.Answers.Enqueue(["Puan veremem."]);
        host.AI.Answers.Enqueue(["{\"score\": 1, \"reason\": \"Yanlış.\"}"]);
        host.AI.Answers.Enqueue(["Score: 3 — kısa ama doğru."]);
        host.Dialogs.JudgeScript = async judge =>
        {
            Assert.Equal(("3 records", true, true), (judge.RecordsText, judge.HasManyRecords, judge.Compare));
            Assert.Equal("Choose the records and press Judge: the model rates each one from 1 to 5 and says why.", judge.SummaryText);
            judge.Compare = false;
            await judge.StartCommand.ExecuteAsync(null);

            var (kargo, second, iade) = (judge.Records[0], judge.Records[1], judge.Records[2]);
            Assert.Equal((WritingState.Done, "4/5", "İyi."), (kargo.State, kargo.StateText, kargo.Reason));
            Assert.Equal((WritingState.Unreadable, "Unreadable", "Puan veremem."), (second.State, second.StateText, second.RawAnswer));
            Assert.Equal((WritingState.Done, true), (iade.State, iade.IsPoor));
            Assert.Equal($"2 of 3 judged · average {Decimal(2.5)} · 1 low", judge.SummaryText);
            Assert.Equal("2 records judged.", judge.ProgressText);
            Assert.True(judge.StartCommand.CanExecute(null));

            await judge.StartCommand.ExecuteAsync(null);
            Assert.Equal((WritingState.Done, "3/5", "kısa ama doğru."), (second.State, second.StateText, second.Reason));
            Assert.False(judge.StartCommand.CanExecute(null));

            // A record is read when it is shown.
            judge.SelectedRecord = iade;
            await judge.PendingWork;
            Assert.Equal(("İade süresi nedir?", "14 gün."), (iade.Question, iade.Answer));
            judge.CloseCommand.Execute(null);
        };

        // From the batch panel.
        host.Main.Details.JudgeSelectionCommand.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Status.Message == "Judged 3 records in “Dataset”");

        Assert.All(host.AI.Requests, r => Assert.DoesNotContain("contradict", r.Request.Messages[0].Content, StringComparison.Ordinal));
        Assert.Equal([4, 3, 1, null], host.Main.Records.Items.Select(r => r.JudgeScore));
        var overview = host.Main.Overview;
        Assert.True(overview.HasJudgments);
        Assert.Equal((Decimal(2.7), "3 of 4 records judged", "Judge 1 records…"), (overview.JudgeAverageText, overview.JudgedText, overview.JudgeRestText));
        Assert.Equal(["5/5", "4/5", "3/5", "2/5", "1/5"], overview.JudgeBars.Select(b => b.Label));
        Assert.Equal([0L, 1, 1, 0, 1], overview.JudgeBars.Select(b => b.Count));
        Assert.Equal([("AI judge: low score (1–2)", 1L)], overview.JudgeFindings.Select(f => (f.Label, f.Count)));

        // The overview's way in: the records not judged as they are now.
        host.Main.Records.SelectedItem = null;
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.Details.HasRecord && !host.Main.Details.IsBatch);
        host.Dialogs.JudgeScript = judge =>
        {
            Assert.Equal("Record #4", judge.RecordsText);
            return Task.CompletedTask;
        };
        overview.JudgeCommand.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => host.Dialogs.LastJudge?.Records.Count == 1);

        overview.ShowJudgedCommand.Execute(overview.JudgeFindings[0]);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.Records.IsLoading && host.Main.Records.Items.Count == 1);
        Assert.Equal(QualityFilter.LowJudgeScore, host.Main.Records.SelectedQualityFilter!.Filter);
        Assert.Equal("AI judge: low score (1–2)", host.Main.Records.SelectedQualityFilter.Label);
    });

    [Fact]
    public Task The_judge_window_stops_reports_server_errors_and_judges_a_record_again() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Chat,
        [
            new ChatPayload("Mağaza asistanısın.", [new ChatMessage(ChatRole.User, "Kargo ücreti?"), new ChatMessage(ChatRole.Assistant, "500 TL üzeri ücretsiz.")]),
            new ChatPayload(null, [new ChatMessage(ChatRole.User, "İade?"), new ChatMessage(ChatRole.Assistant, "14 gün.")]),
        ]);
        host.AddAIConnection();
        var ids = host.Main.Records.Items.Select(r => r.Id).ToList();
        using var judge = await host.Get<Services.RecordJudge>().CreateAsync(new QATarget(dataset.Id, dataset.Name, dataset.Type, null), ids);
        var (kargo, iade) = (judge.Records[0], judge.Records[1]);

        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["{\"score\": 5,", " \"reason\": \"İyi.\"}"]);
        var running = judge.StartCommand.ExecuteAsync(null);
        await DesktopTestHost.WaitUntilAsync(() => kargo.IsWriting && judge.IsRunning);
        Assert.Equal(("Judging…", "Judging with qwen2.5:7b…", "Judging record 1 of 2 with qwen2.5:7b…"), (kargo.StateText, kargo.Activity, judge.ProgressText));
        judge.StopCommand.Execute(null);
        await running;
        Assert.Equal((WritingState.Waiting, "Stopped; what was judged is kept."), (kargo.State, judge.ProgressText));
        host.AI.Hold = null;

        host.AI.Failure = new AIException(AIErrorKind.Unreachable, "down");
        await judge.StartCommand.ExecuteAsync(null);
        Assert.Equal("Nothing answers at http://localhost:11434/v1. Is Ollama running?", judge.AIError);
        Assert.Equal(WritingState.Waiting, kargo.State);
        host.AI.Failure = null;
        judge.DismissAIErrorCommand.Execute(null);

        host.AI.Answers.Enqueue(["{\"score\": 5, \"reason\": \"İyi.\"}"]);
        host.AI.Answers.Enqueue(["{\"score\": 4, \"reason\": \"Kısa.\"}"]);
        await judge.StartCommand.ExecuteAsync(null);
        Assert.Contains("Rate the assistant's replies", host.AI.Requests.Last().Request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(["5/5", "4/5"], judge.Records.Select(r => r.StateText));

        host.AI.Answers.Enqueue(["{\"score\": 2, \"reason\": \"Eksik.\"}"]);
        await judge.RewriteCommand.ExecuteAsync(iade);
        Assert.Equal(("2/5", "Eksik."), (iade.StateText, iade.Reason));
        Assert.Equal(ids.Order(), judge.JudgedIds.Order());

        Localizer.Instance.SetCulture(Localizer.Turkish);
        Assert.Equal($"2 kayıttan 2 tanesi değerlendirildi · ortalama {Decimal(3.5)} · 1 düşük puan", judge.SummaryText);
        Assert.Equal("Turkish", host.Get<Services.RecordJudge>().Language);
        Localizer.Instance.SetCulture(Localizer.English);
    });
}
