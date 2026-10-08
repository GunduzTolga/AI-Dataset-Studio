using AIDatasetStudio.Application.Tags;
using AIDatasetStudio.Desktop.Controls;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Desktop.Views.Dialogs;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// Tags in the real window: Mod+1…5 while typing, the chips and picker of the editor, the pills
/// of the list and Record › Tag.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class TagViewTests
{
    public TagViewTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Mod_1_in_an_editor_field_toggles_the_first_tag() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(2);
        await host.Get<ITagService>().CreateAsync("hukuk", TestContext.Current.CancellationToken);
        await host.Main.Tags.ReloadAsync();
        var window = Open(host);
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);

        var field = Visible<CodeEditor>(window).First(box => box.Classes.Contains("field"));
        field.TextArea.Focus(); // an editor field takes the focus in its text area
        Press(window, Key.D1, RawInputModifiers.Control);
        await DesktopTestHost.WaitUntilAsync(() => row.Tags.Count == 1);

        Assert.Equal("Soru 1", field.Text); // no digit typed
        Assert.Contains(Visible<TextBlock>(window), block => block.Text == "hukuk" && block.FindAncestorOfType<ItemsControl>()?.Name == "RecordTagList");
        Assert.Contains(Visible<TextBlock>(window), block => block.Text == "hukuk" && block.FindAncestorOfType<ListBox>()?.Name == "RecordList");

        Press(window, Key.NumPad1, RawInputModifiers.Control);
        await DesktopTestHost.WaitUntilAsync(() => row.Tags.Count == 0);

        // Record › Tag lists the tags with their shortcuts.
        var menu = window.FindControl<MenuItem>("TagMenuItem")!;
        Assert.True(menu.IsEnabled);
        ((MenuItem)menu.Parent!).IsSubMenuOpen = true;
        menu.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        var item = Assert.IsType<MenuItem>(menu.ContainerFromIndex(0));
        Assert.Equal("hukuk", item.Header);
        Assert.Equal(new KeyGesture(Key.D1, KeyModifiers.Control), item.InputGesture);
        window.Close();
    });

    [Fact]
    public Task The_picker_opens_under_its_button_creates_a_tag_and_closes_with_Escape() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(1);
        var window = Open(host);
        var row = host.Main.Records.Items[0];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        Assert.False(window.FindControl<MenuItem>("TagMenuItem")!.IsEnabled); // no tags yet

        var button = Visible<Button>(window).Single(b => b.Name == "AddTagButton");
        button.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var popup = window.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "TagPopup");
        Assert.True(popup.IsOpen);

        var query = popup.Child!.GetVisualDescendants().OfType<TextBox>().Single();
        query.Focus();
        window.KeyTextInput("kategori:teknik");
        Press(window, Key.Enter, RawInputModifiers.None);
        await DesktopTestHost.WaitUntilAsync(() => row.Tags.Count == 1);
        Assert.Equal("kategori:teknik", row.Tags[0].Name);
        Assert.True(popup.IsOpen);

        query.Focus();
        Press(window, Key.Escape, RawInputModifiers.None);
        Assert.False(popup.IsOpen);
        Assert.False(host.Main.Details.TagPicker.IsOpen);
        window.Close();
    });

    [Fact]
    public Task The_tag_manager_window_lists_tags_with_their_group_and_shortcut() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateWorkspaceAsync();
        var tags = host.Get<ITagService>();
        await tags.CreateAsync("kategori:hukuk", TestContext.Current.CancellationToken);
        await tags.CreateAsync("teknik", TestContext.Current.CancellationToken);
        var manager = new TagManagerViewModel(tags, host.Main.Tags, host.Dialogs, Localizer.Instance);
        var window = new TagManagerWindow { DataContext = manager };

        window.Show();
        await DesktopTestHost.WaitUntilAsync(() => manager.Items.Count == 2);

        var texts = Visible<TextBlock>(window).Select(block => block.Text).ToList();
        Assert.Contains("kategori:hukuk", texts);
        Assert.Contains("kategori", texts);
        Assert.Equal(["Ctrl+1", "Ctrl+2"], Visible<ComboBox>(window).Select(box => ((ShortcutOption)box.SelectedItem!).Label));

        manager.CloseCommand.Execute(null);
        Assert.False(window.IsVisible);
    });

    private static MainWindow Open(DesktopTestHost host)
    {
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static List<T> Visible<T>(TopLevel window)
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
