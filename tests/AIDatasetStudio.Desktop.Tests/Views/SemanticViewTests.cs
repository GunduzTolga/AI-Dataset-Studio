using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.Tests.ViewModels;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Desktop.Views.Dialogs;
using AIDatasetStudio.Domain.Enums;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The overview's SIMILAR IN MEANING card and the embedding model of a connection, rendered (M16).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class SemanticViewTests
{
    public SemanticViewTests() => Localizer.Instance.SetCulture(Localizer.Turkish);

    [Fact]
    public Task The_card_turns_comparing_by_meaning_on_and_shows_the_threshold() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, SemanticTests.Samples);
        await host.AnalyzeAsync();
        host.AddAIConnection("bge-m3");
        host.AI.Embedder = text => SemanticTests.Vectors[text];
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "ANLAMCA BENZER SORULAR");
        Assert.StartsWith("Sorular ifadelerine göre karşılaştırılıyor.", Find<TextBlock>(window, "SemanticStatusText").Text, StringComparison.Ordinal);
        Assert.False(Find<NumericUpDown>(window, "SemanticThresholdBox").IsEffectivelyVisible);
        var run = Find<Button>(window, "SemanticRunButton");
        Assert.True(run.IsEffectivelyVisible);
        Assert.Equal("Veri setindeki soruları AI bağlantınızdaki embedding modeline gönderir.", ToolTip.GetTip(run));
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Anlamca karşılaştır");

        run.Command!.Execute(null);
        await host.Main.Overview.SemanticRun;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("4 sorunun tümü anlamca karşılaştırılıyor.", Find<TextBlock>(window, "SemanticStatusText").Text);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Model: bge-m3");
        Assert.True(Find<NumericUpDown>(window, "SemanticThresholdBox").IsEffectivelyVisible);
        Assert.Equal(0.9m, Find<NumericUpDown>(window, "SemanticThresholdBox").Value);
        Assert.False(Find<Button>(window, "SemanticRunButton").IsEffectivelyVisible);
        Assert.True(Find<Button>(window, "SemanticOffButton").IsEffectivelyVisible);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Benzer sorular");

        // The editor names the other record, whichever way they are alike.
        var row = host.Main.Records.Items[1];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Details.QualityIssues.Count > 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "#1 numaralı kayıtla neredeyse aynı soru (ifade ya da anlam olarak).");
        window.Close();
    });

    [Fact]
    public Task The_connections_window_has_a_field_for_the_embedding_model() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        host.AddAIConnection("bge-m3");
        var connections = host.Get<Services.AIAssistant>().CreateConnections();
        var window = new AIConnectionsWindow { DataContext = connections, Width = 980, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var box = Find<ComboBox>(window, "EmbeddingModelBox");
        Assert.True(box.IsEffectivelyVisible);
        Assert.Equal("bge-m3", box.Text);
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Embedding modeli (isteğe bağlı)");
        window.Close();
        await Task.CompletedTask;
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
