using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Documents;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Desktop.Views.Dialogs;
using AIDatasetStudio.Domain.Enums;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The Q&amp;A extraction window and the source passage in the editor, rendered (M13).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class QAExtractionWindowTests
{
    public QAExtractionWindowTests() => Localizer.Instance.SetCulture(Localizer.Turkish);

    [Fact]
    public Task The_window_shows_passages_and_pairs_and_asks_before_dropping_them() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, []);
        host.AddAIConnection();
        var path = Path.Combine(host.Directory, "kılavuz.md");
        await File.WriteAllTextAsync(path, "# Kargo\n\nKargo 500 TL üzeri siparişlerde ücretsizdir.\n");
        var extraction = host.Get<DocumentQuestions>().Create(path, new QATarget(dataset.Id, dataset.Name, dataset.Type, null));
        var window = new QAExtractionWindow { DataContext = extraction, Width = 1080, Height = 740 };
        window.Show();
        await DesktopTestHost.WaitUntilAsync(() => extraction.HasChunks);

        Assert.Equal("kılavuz.md", Find<TextBlock>(window, "FileNameText").Text);
        Assert.Equal("qwen2.5:7b · Ollama", Find<TextBlock>(window, "ConnectionText").Text);
        Assert.Single(Find<ListBox>(window, "ChunkList").Items);
        Assert.Equal("# Kargo\n\nKargo 500 TL üzeri siparişlerde ücretsizdir.", Find<SelectableTextBlock>(window, "PassageText").Text);
        Assert.False(Find<Button>(window, "AddButton").IsEffectivelyEnabled);

        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo ücreti ne kadar?\", \"answer\": \"500 TL üzeri ücretsiz.\"}]"]);
        Find<Button>(window, "StartButton").Command!.Execute(null);
        await extraction.PendingWork;
        Dispatcher.UIThread.RunJobs();

        var boxes = Visible<TextBox>(window);
        Assert.Contains(boxes, b => b.Text == "Kargo ücreti ne kadar?");
        Assert.Contains(boxes, b => b.Text == "500 TL üzeri ücretsiz.");
        Assert.Equal("1 kaydı ekle", Find<Button>(window, "AddButton").Content);
        Assert.True(Find<Button>(window, "AddButton").IsEffectivelyEnabled);
        Assert.False(Find<NumericUpDown>(window, "ChunkSizeBox").IsEffectivelyEnabled);

        // Closing from the title bar asks first, and keeps the window when the user says no.
        host.Dialogs.ConfirmAnswer = false;
        window.Close();
        await DesktopTestHost.WaitUntilAsync(() => host.Dialogs.ConfirmMessages.Count == 1);
        Assert.True(window.IsVisible);
        host.Dialogs.ConfirmAnswer = true;
        window.Close();
        await DesktopTestHost.WaitUntilAsync(() => !window.IsVisible);
        Assert.Equal("1 soru-cevap henüz veri setine eklenmedi. Atılsın mı?", host.Dialogs.ConfirmMessages[^1]);
    });

    [Fact]
    public Task The_editor_shows_the_passage_a_record_was_written_from() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, []);
        host.AddAIConnection();
        host.Files.DocumentPath = Path.Combine(host.Directory, "kılavuz.md");
        await File.WriteAllTextAsync(host.Files.DocumentPath, "# Kargo\n\nKargo 500 TL üzeri siparişlerde ücretsizdir.\n");
        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo ücreti ne kadar?\", \"answer\": \"500 TL üzeri ücretsiz.\"}]"]);
        host.Dialogs.QAExtractionScript = async extraction =>
        {
            await extraction.InitializeAsync();
            await extraction.StartCommand.ExecuteAsync(null);
            await extraction.AddCommand.ExecuteAsync(null);
        };
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();

        await host.Main.ExtractQuestionsCommand.ExecuteAsync(null);
        host.Main.Records.SelectedItem = host.Main.Records.Items[0];
        await host.WaitForDetailsAsync(host.Main.Records.Items[0].Id);
        Dispatcher.UIThread.RunJobs();

        var toggle = Find<ToggleButton>(window, "SourceToggle");
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.False(Find<Border>(window, "SourcePassage").IsEffectivelyVisible);
        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(Find<Border>(window, "SourcePassage").IsEffectivelyVisible);
        Assert.Contains(Visible<SelectableTextBlock>(window), t => t.Text == "# Kargo\n\nKargo 500 TL üzeri siparişlerde ücretsizdir.");
        Assert.Contains(Visible<SelectableTextBlock>(window), t => t.Text == "Kargo · satır 1–3");
        Assert.Contains(Visible<SelectableTextBlock>(window), t => t.Text == "Belgeden çıkarıldı (kılavuz.md#line=1)");
        window.Close();
    });

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
