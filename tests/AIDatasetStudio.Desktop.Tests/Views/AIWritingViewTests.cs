using AIDatasetStudio.Desktop.Controls;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// Writing with AI in the real editor (M12): Mod+G, the card buttons and the locked field.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class AIWritingViewTests
{
    public AIWritingViewTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Mod_G_writes_the_output_while_the_field_is_locked() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Kargo ücreti?", "", "")]);
        host.AddAIConnection();
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        Dispatcher.UIThread.RunJobs();

        // Only the answer side offers the button.
        var writeButtons = Visible<Button>(window).Where(b => ToolTip.GetTip(b) as string == "Write with AI").ToList();
        Assert.Single(writeButtons);
        var output = Visible<CodeEditor>(window)[2];
        Assert.Same(writeButtons[0].FindAncestorOfType<Border>(), output.FindAncestorOfType<Border>());

        host.AI.Hold = new TaskCompletionSource();
        host.AI.Answers.Enqueue(["500 TL üzeri ", "ücretsiz."]);
        Visible<CodeEditor>(window)[0].TextArea.Focus();
        Press(window, Key.G, RawInputModifiers.Control);
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Details.IsWriting && host.AI.Requests.Count == 1);
        Dispatcher.UIThread.RunJobs();

        Assert.True(output.IsReadOnly);
        Assert.Contains(Visible<Button>(window), b => ToolTip.GetTip(b) as string == "Stop");
        Assert.Contains(Visible<TextBlock>(window), t => t.Text == "Writing with qwen2.5:7b…");

        host.AI.Hold.SetResult();
        await host.Main.Details.Writing;
        Dispatcher.UIThread.RunJobs();

        Assert.False(output.IsReadOnly);
        Assert.Equal("500 TL üzeri ücretsiz.", output.Text);
        Assert.Equal("Kargo ücreti?", Visible<CodeEditor>(window)[0].Text); // the shortcut typed nothing
        window.Close();
    });

    private static List<T> Visible<T>(Window window)
        where T : Control
    {
        window.UpdateLayout();
        return [.. window.GetVisualDescendants().OfType<T>().Where(control => control.IsEffectivelyVisible)];
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
}
