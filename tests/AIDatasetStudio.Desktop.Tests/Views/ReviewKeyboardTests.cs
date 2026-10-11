using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Controls;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
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
/// The review shortcuts (SDD §6.2) pressed in a real window, including inside editor fields.
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class ReviewKeyboardTests
{
    public ReviewKeyboardTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Mod_Enter_in_an_editor_field_approves_and_moves_on_without_a_line_break() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var window = Open(host);
        var records = host.Main.Records;
        await SelectAsync(host, 0);

        var field = EditorFields(window)[0];
        field.TextArea.Focus();
        field.CaretOffset = field.Text!.Length;
        window.KeyTextInput("!");
        Press(window, Key.Enter, RawInputModifiers.Control);
        await host.WaitForDetailsAsync(records.Items[1].Id);

        Assert.Equal(RecordStatus.Approved, records.Items[0].Status);
        var saved = await host.Get<IRecordQueryService>().GetAsync(records.Items[0].Id);
        Assert.Equal("Soru 1!", ((InstructionPayload)saved!.Payload).Instruction);
        window.Close();
    });

    [Fact]
    public Task Mod_Z_while_typing_is_left_to_the_text_box() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(2);
        var window = Open(host);
        var records = host.Main.Records;
        await SelectAsync(host, 0);
        await host.Main.Details.ApproveCommand.ExecuteAsync(null);
        var step = host.Get<UndoHistory>().NextUndo;

        EditorFields(window)[0].TextArea.Focus();
        Press(window, Key.Z, RawInputModifiers.Control);
        Assert.Same(step, host.Get<UndoHistory>().NextUndo);
        Assert.Equal(RecordStatus.Approved, records.Items[0].Status);

        FocusSelectedRow(window);
        Press(window, Key.Z, RawInputModifiers.Control);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.IsBusy && records.Items.Count == 2 && records.Items[0].Status == RecordStatus.Draft);

        Press(window, Key.Y, RawInputModifiers.Control);
        await DesktopTestHost.WaitUntilAsync(() => !host.Main.IsBusy && records.Items.Count == 2 && records.Items[0].Status == RecordStatus.Approved);
        window.Close();
    });

    [Fact]
    public Task F8_moves_between_records_from_anywhere() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var window = Open(host);
        var records = host.Main.Records;
        await SelectAsync(host, 0);
        EditorFields(window)[2].TextArea.Focus();

        Press(window, Key.F8, RawInputModifiers.None);
        await host.WaitForDetailsAsync(records.Items[1].Id);
        Press(window, Key.F8, RawInputModifiers.None);
        await host.WaitForDetailsAsync(records.Items[2].Id);
        Press(window, Key.F8, RawInputModifiers.Shift);
        await host.WaitForDetailsAsync(records.Items[1].Id);
        window.Close();
    });

    [Fact]
    public Task Delete_removes_records_only_when_the_list_has_focus() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(3);
        var window = Open(host);
        var records = host.Main.Records;
        await SelectAsync(host, 1);

        EditorFields(window)[0].TextArea.Focus();
        Press(window, Key.Delete, RawInputModifiers.None);
        Assert.Empty(host.Dialogs.ConfirmMessages);

        FocusSelectedRow(window);
        Press(window, Key.Delete, RawInputModifiers.None);
        await DesktopTestHost.WaitUntilAsync(() => records.Items.Count == 2);

        Assert.Single(host.Dialogs.ConfirmMessages);
        Assert.Equal([1L, 3L], records.Items.Select(r => r.Ordinal));
        window.Close();
    });

    [Fact]
    public Task Mod_F_focuses_the_search_box_and_menus_show_the_gestures() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(1);
        var window = Open(host);

        Press(window, Key.F, RawInputModifiers.Control);

        Assert.Equal("SearchBox", (window.FocusManager?.GetFocusedElement() as TextBox)?.Name);
        Assert.Equal(new KeyGesture(Key.Enter, KeyModifiers.Control), window.FindControl<MenuItem>("ApproveNextMenuItem")!.InputGesture);
        Assert.Equal(new KeyGesture(Key.Back, KeyModifiers.Control | KeyModifiers.Shift), window.FindControl<MenuItem>("ArchiveNextMenuItem")!.InputGesture);
        Assert.Equal(new KeyGesture(Key.Z, KeyModifiers.Control), window.FindControl<MenuItem>("UndoMenuItem")!.InputGesture);
        window.Close();
    });

    [Fact]
    public Task The_inspector_shows_the_overview_until_a_record_is_selected_and_Mod_Shift_M_masks_it() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Kime yazayım?", "", "ali@firma.com adresine yazın.")]);
        var window = Open(host);
        var overview = window.GetVisualDescendants().OfType<DatasetOverviewView>().Single();
        var details = window.GetVisualDescendants().OfType<RecordDetailsView>().Single();
        Assert.True(overview.IsVisible);
        Assert.False(details.IsVisible);

        await SelectAsync(host, 0);
        Assert.False(overview.IsVisible);
        Assert.True(details.IsVisible);

        EditorFields(window)[2].TextArea.Focus();
        Press(window, Key.M, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(host.Main.Details.IsPiiPreview);
        Assert.DoesNotContain(EditorFields(window), box => box.IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), block => block.Text == "[EMAIL_1] adresine yazın." && block.IsEffectivelyVisible);

        Press(window, Key.M, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(host.Main.Details.IsPiiPreview);
        Assert.Equal(3, EditorFields(window).Count(box => box.IsEffectivelyVisible));
        window.Close();
    });

    [Fact]
    public Task Mod_E_opens_the_export_wizard() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(2);
        var window = Open(host);
        host.Dialogs.ExportWizardScript = _ => Task.CompletedTask;
        Assert.Equal(new KeyGesture(Key.E, KeyModifiers.Control), window.FindControl<MenuItem>("ExportMenuItem")!.InputGesture);

        Press(window, Key.E, RawInputModifiers.Control);
        await DesktopTestHost.WaitUntilAsync(() => host.Dialogs.LastExportWizard is not null);

        Assert.Equal("Dataset", host.Dialogs.LastExportWizard!.Target.Name);
        window.Close();
    });

    [Fact]
    public Task Mod_Shift_N_adds_a_record_and_puts_the_keyboard_in_its_first_field() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetWithRecordsAsync(1);
        var window = Open(host);
        Assert.Equal(new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift), window.FindControl<MenuItem>("NewRecordMenuItem")!.InputGesture);

        Press(window, Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
        await DesktopTestHost.WaitUntilAsync(() => host.Main.Records.Items.Count == 2 && host.Main.Details.Record?.Ordinal == 2);
        await DesktopTestHost.WaitUntilAsync(() => window.FocusManager?.GetFocusedElement() == EditorFields(window)[0].TextArea);

        window.Close();
    });

    private static MainWindow Open(DesktopTestHost host)
    {
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task SelectAsync(DesktopTestHost host, int index)
    {
        var row = host.Main.Records.Items[index];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<CodeEditor> EditorFields(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<CodeEditor>().Where(box => box.Classes.Contains("field")).ToList();
    }

    // Rows take keyboard focus (the list itself is not focusable), as when a row is clicked.
    private static void FocusSelectedRow(Window window)
    {
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "RecordList");
        Assert.True(list.ContainerFromIndex(list.SelectedIndex)?.Focus());
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
}
