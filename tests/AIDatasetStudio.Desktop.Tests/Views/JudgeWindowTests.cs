using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Desktop.Views.Dialogs;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The judge window, the editor's judgment and the overview card, rendered (M15).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class JudgeWindowTests
{
    public JudgeWindowTests() => Localizer.Instance.SetCulture(Localizer.Turkish);

    [Fact]
    public Task The_window_lists_scores_and_shows_the_reason_of_the_selected_record() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz."),
            new InstructionPayload("İade süresi nedir?", "", "14 gün."),
        ]);
        host.AddAIConnection();
        var judge = await host.Get<RecordJudge>().CreateAsync(
            new QATarget(dataset.Id, dataset.Name, dataset.Type, null), [.. host.Main.Records.Items.Select(r => r.Id)]);
        var window = new JudgeWindow { DataContext = judge, Width = 1080, Height = 740 };
        window.Show();
        await judge.PendingWork;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("2 kayıt", Find<TextBlock>(window, "RecordsText").Text);
        Assert.True(Find<ListBox>(window, "RecordList").IsEffectivelyVisible);
        Assert.Equal("Kargo ücreti ne kadar?", Find<SelectableTextBlock>(window, "QuestionText").Text);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Henüz değerlendirilmedi.");

        host.AI.Answers.Enqueue(["{\"score\": 4, \"reason\": \"Doğru ama kısa.\"}"]);
        host.AI.Answers.Enqueue(["{\"score\": 1, \"reason\": \"Yanlış.\"}"]);
        Find<Button>(window, "StartButton").Command!.Execute(null);
        await judge.PendingWork;
        judge.SelectedRecord = judge.Records[0];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Doğru ama kısa.", Find<SelectableTextBlock>(window, "ReasonText").Text);
        Assert.Contains("good", Find<Border>(window, "ScorePill").Classes);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "4/5");
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "1/5");
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Değerlendirmeler geldikçe kaydedilir.");
        Assert.StartsWith("2 kayıttan 2 tanesi değerlendirildi", Find<TextBlock>(window, "SummaryText").Text, StringComparison.Ordinal);

        Find<Button>(window, "CloseButton").Command!.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => !window.IsVisible);
    });

    [Fact]
    public Task The_editor_shows_the_judgment_and_the_shortcuts_start_and_stop_the_model() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Kargo ücreti ne kadar?", "", "500 TL üzeri ücretsiz.")]);
        host.AddAIConnection();
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        Dispatcher.UIThread.RunJobs();
        var details = host.Main.Details;

        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "MODEL DEĞERLENDİRMESİ");
        Assert.False(Find<Border>(window, "JudgeScorePill").IsEffectivelyVisible);
        Assert.Equal("Modelle Değerlendir", window.FindControl<MenuItem>("JudgeMenuItem")!.Header);
        Assert.Equal(new KeyGesture(Key.J, KeyModifiers.Control), window.FindControl<MenuItem>("JudgeMenuItem")!.InputGesture);

        // Ctrl+J starts judging; pressed again while the model answers, it stops.
        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["{\"score\": ", "3}"]);
        Press(window, Key.J);
        await DesktopTestHost.WaitUntilAsync(() => details.IsJudging);
        Assert.Equal("qwen2.5:7b ile değerlendiriliyor…", Find<TextBlock>(window, "JudgingText").Text);
        Press(window, Key.J);
        await DesktopTestHost.WaitUntilAsync(() => !details.IsJudging);
        Assert.False(details.HasJudgment);
        host.AI.Hold = null;

        host.AI.Answers.Enqueue(["{\"score\": 2, \"reason\": \"Ücretli siparişler eksik.\"}"]);
        Press(window, Key.J);
        await DesktopTestHost.WaitUntilAsync(() => row.JudgeScore == 2);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Border>(window, "JudgeScorePill").IsEffectivelyVisible);
        Assert.Equal("Ücretli siparişler eksik.", Find<SelectableTextBlock>(window, "JudgeReasonText").Text);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Yeniden değerlendir");
        Assert.Contains(Visible<Border>(window), b => ToolTip.GetTip(b) as string == "Model puanı: 2/5");

        // Ctrl+G stops writing the same way (it could not while the command ran).
        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["Yeni", " yanıt"]);
        Press(window, Key.G);
        await DesktopTestHost.WaitUntilAsync(() => details.IsWriting);
        Press(window, Key.G);
        await DesktopTestHost.WaitUntilAsync(() => !details.IsWriting);
        Assert.Equal("Yeni", details.Fields[3].Text);
        host.AI.Hold = null;
        details.DiscardCommand.Execute(null);

        // The overview's card.
        host.Main.Records.SelectedItem = null;
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Overview.Judgments is { JudgedCount: 1 });
        Dispatcher.UIThread.RunJobs();
        Assert.True(Find<Border>(window, "JudgeAverageTile").IsEffectivelyVisible);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Model: düşük puan (1–2)");
        Assert.False(Find<Button>(window, "JudgeRestButton").IsEffectivelyVisible);
        window.Close();
    });

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.Control, PhysicalKey.None, null);
        window.KeyRelease(key, RawInputModifiers.Control, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Window window, string name)
        where T : Control
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
    }

    private static List<T> Visible<T>(Window window)
        where T : Control
    {
        window.UpdateLayout();
        return [.. window.GetVisualDescendants().OfType<T>().Where(control => control.IsEffectivelyVisible)];
    }
}
