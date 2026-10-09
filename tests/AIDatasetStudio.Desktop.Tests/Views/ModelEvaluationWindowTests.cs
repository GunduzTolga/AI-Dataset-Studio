using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.Views.Dialogs;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The Evaluate a Model window, rendered (M22).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ModelEvaluationWindowTests
{
    public ModelEvaluationWindowTests() => Localizer.Instance.SetCulture(Localizer.Turkish);

    [Fact]
    public Task Closing_the_window_while_the_model_answers_stops_it() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("İade süresi?", "", "14 gün içinde."),
            new InstructionPayload("Değişim süresi?", "", "14 gün içinde."),
        ]);
        host.AddAIConnection();
        var evaluation = await host.Get<ModelEvaluator>().CreateAsync(dataset.Id, dataset.Name, dataset.Type, TestContext.Current.CancellationToken);
        evaluation.IncludeUnapproved = true;
        var window = new ModelEvaluationWindow { DataContext = evaluation };
        window.Show();
        Assert.EndsWith(" · Talimat", Find<TextBlock>(window, "TargetText").Text, StringComparison.Ordinal);

        // Never exported: all records, with the warning that a trained model has seen them.
        Assert.Equal(["Bütün kayıtlar"], evaluation.Sources.Select(s => s.Label));
        Assert.Contains("warning", Find<TextBlock>(window, "SourceHint").Classes);

        // No earlier evaluation yet: the history says so.
        Assert.Equal("Önceki değerlendirmeler (0)", Find<ToggleButton>(window, "HistoryToggle").GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Find<ToggleButton>(window, "HistoryToggle").IsChecked = true;
        Assert.True(evaluation.ShowHistory);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text?.StartsWith("Bu veri seti henüz değerlendirilmedi.", StringComparison.Ordinal) == true);

        // Evaluating shows the questions again.
        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["14 gün ", "içinde."]);
        Find<Button>(window, "RunButton").Command!.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => evaluation.Rows is [{ Answer: "14 gün " }]);
        Assert.True(evaluation.IsRunning);
        Assert.False(evaluation.ShowHistory);

        // The language follows the app while the window is open.
        Localizer.Instance.SetCulture(Localizer.English);
        Dispatcher.UIThread.RunJobs();
        Assert.EndsWith(" · Instruction", Find<TextBlock>(window, "TargetText").Text, StringComparison.Ordinal);

        // Closing from the title bar stops the model: nothing more is asked.
        window.Close();
        await evaluation.PendingWork.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(evaluation.IsRunning);
        Assert.Single(host.AI.Requests);
        Assert.Equal("Stopped after 0 questions.", evaluation.ProgressText);

        // A stopped evaluation is not kept.
        Assert.Empty(evaluation.History);
    });

    private static IEnumerable<T> Visible<T>(Window window)
        where T : Control
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible);
    }

    private static T Find<T>(Window window, string name)
        where T : Control
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
    }
}
