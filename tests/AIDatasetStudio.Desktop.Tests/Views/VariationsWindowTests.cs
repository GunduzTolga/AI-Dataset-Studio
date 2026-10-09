using AIDatasetStudio.Application.Records;
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
/// The Generate Variations window and its ways in, rendered (M14).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class VariationsWindowTests
{
    public VariationsWindowTests() => Localizer.Instance.SetCulture(Localizer.Turkish);

    [Fact]
    public Task The_window_shows_the_record_and_its_variants_and_flags_the_close_ones() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        var dataset = await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Kargo ücreti ne kadar?", "Sipariş 300 TL.", "49,90 TL.")]);
        host.AddAIConnection();
        var record = await host.Get<IRecordQueryService>().GetAsync(host.Main.Records.Items[0].Id);
        var variations = host.Get<RecordVariations>().Create(new QATarget(dataset.Id, dataset.Name, dataset.Type, null), [record!]);
        var window = new VariationsWindow { DataContext = variations, Width = 1080, Height = 740 };
        window.Show();

        Assert.Equal("#1 kaydı", Find<TextBlock>(window, "RecordsText").Text);
        Assert.Equal("qwen2.5:7b · Ollama", Find<TextBlock>(window, "ConnectionText").Text);
        Assert.False(Find<ListBox>(window, "RecordList").IsEffectivelyVisible);
        Assert.Equal("Kargo ücreti ne kadar?", Find<SelectableTextBlock>(window, "OriginalQuestion").Text);
        Assert.Contains(Visible<SelectableTextBlock>(window), t => t.Text == "Sipariş 300 TL.");
        Assert.False(Find<Button>(window, "AddButton").IsEffectivelyEnabled);

        host.AI.Answers.Enqueue(["[{\"question\": \"Kargo kaç para?\"}, {\"question\": \"Kargo ücreti ne kadardır?\"}]"]);
        Find<Button>(window, "StartButton").Command!.Execute(null);
        await variations.PendingWork;
        Dispatcher.UIThread.RunJobs();

        var boxes = Find<ItemsControl>(window, "VariantList").GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible);
        Assert.Equal(["Kargo kaç para?", "Kargo ücreti ne kadardır?"], boxes.Select(b => b.Text));
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Asıl soruya çok yakın");
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Her varyant asıl yanıtı korur.");
        Assert.Equal("1 kaydı ekle", Find<Button>(window, "AddButton").Content);
        Assert.Equal("2 varyanttan 1 tanesi taslak kayıt olarak eklenecek", Find<TextBlock>(window, "SummaryText").Text);

        // Closing from the title bar asks first.
        host.Dialogs.ConfirmAnswer = true;
        window.Close();
        await DesktopTestHost.WaitUntilAsync(() => !window.IsVisible);
        Assert.Equal("1 varyant henüz veri setine eklenmedi. Atılsın mı?", host.Dialogs.ConfirmMessages[^1]);
    });

    [Fact]
    public Task The_editor_the_batch_panel_and_the_record_menu_lead_to_variations() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("Kargo ücreti ne kadar?", "", "Ücretsiz."),
            new InstructionPayload("İade süresi nedir?", "", "14 gün."),
        ]);
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();

        host.Main.Records.SelectedItem = host.Main.Records.Items[0];
        await host.WaitForDetailsAsync(host.Main.Records.Items[0].Id);
        Dispatcher.UIThread.RunJobs();
        var button = Find<Button>(window, "VariationsButton");
        Assert.True(button.IsEffectivelyVisible);
        Assert.Equal("Varyant üret: aynı soru başka sözlerle", ToolTip.GetTip(button));
        button.Command!.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => host.Dialogs.LastVariations is not null);
        Assert.Equal("#1 kaydı", host.Dialogs.LastVariations!.RecordsText);

        // Record › Generate Variations, Mod+Shift+G (also while typing in the editor).
        var menu = window.FindControl<MenuItem>("VariationsMenuItem")!;
        Assert.Equal("Varyant Üret…", menu.Header);
        Assert.Equal(new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Shift), menu.InputGesture);
        var first = host.Dialogs.LastVariations;
        window.KeyPress(Key.G, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        window.KeyRelease(Key.G, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        await DesktopTestHost.WaitUntilAsync(() => host.Dialogs.LastVariations != first);

        host.Main.Records.Select([.. host.Main.Records.Items.Select(r => r.Id)]);
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Details.IsBatch);
        Dispatcher.UIThread.RunJobs();
        var batch = Find<Button>(window, "BatchVariationsButton");
        Assert.True(batch.IsEffectivelyVisible);
        batch.Command!.Execute(null);
        await DesktopTestHost.WaitUntilAsync(() => host.Dialogs.LastVariations!.Records.Count == 2);
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
