using System.Windows.Input;
using AIDatasetStudio.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIDatasetStudio.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly List<Shortcut> _shortcuts = [];

    public MainWindow()
    {
        InitializeComponent();
        ConfigureHotKeys();
        // Tunnel: shortcuts are seen before a focused text box can turn Mod+Enter into a line break.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.NewRecordShown += (_, _) => RecordDetailsView.FocusFirstField();
            }
        };
    }

    /// <param name="Gesture">The key combination.</param>
    /// <param name="Command">The command it runs.</param>
    /// <param name="InTextBox">Whether it also works while typing (otherwise the text box keeps the key, e.g. its own undo).</param>
    /// <param name="When">An extra condition, e.g. the record list has focus.</param>
    /// <param name="Parameter">The command parameter, e.g. the tag slot of Mod+1…5.</param>
    private sealed record Shortcut(KeyGesture Gesture, Func<MainWindowViewModel, ICommand> Command, bool InTextBox, Func<bool>? When = null, object? Parameter = null);

    // Mod is Ctrl on Windows/Linux and Cmd on macOS (SDD §6.2).
    private void ConfigureHotKeys()
    {
        var mod = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        NewWorkspaceMenuItem.HotKey = NewWorkspaceMenuItem.InputGesture = new KeyGesture(Key.N, mod);
        OpenWorkspaceMenuItem.HotKey = OpenWorkspaceMenuItem.InputGesture = new KeyGesture(Key.O, mod);
        Add(ExportMenuItem, new KeyGesture(Key.E, mod), vm => vm.ExportCommand, inTextBox: true);
        Add(NewRecordMenuItem, new KeyGesture(Key.N, mod | KeyModifiers.Shift), vm => vm.NewRecordCommand, inTextBox: true);

        Add(UndoMenuItem, new KeyGesture(Key.Z, mod), vm => vm.UndoCommand, inTextBox: false);
        Add(RedoMenuItem, new KeyGesture(Key.Z, mod | KeyModifiers.Shift), vm => vm.RedoCommand, inTextBox: false);
        _shortcuts.Add(new Shortcut(new KeyGesture(Key.Y, mod), vm => vm.RedoCommand, InTextBox: false));
        Add(SaveMenuItem, new KeyGesture(Key.S, mod), vm => vm.Details.SaveCommand, inTextBox: true);
        Add(ApproveNextMenuItem, new KeyGesture(Key.Enter, mod), vm => vm.ApproveAndNextCommand, inTextBox: true);
        Add(ArchiveNextMenuItem, new KeyGesture(Key.Back, mod | KeyModifiers.Shift), vm => vm.ArchiveAndNextCommand, inTextBox: true);
        Add(NextMenuItem, new KeyGesture(Key.F8), vm => vm.NextRecordCommand, inTextBox: true);
        Add(PreviousMenuItem, new KeyGesture(Key.F8, KeyModifiers.Shift), vm => vm.PreviousRecordCommand, inTextBox: true);
        Add(PiiPreviewMenuItem, new KeyGesture(Key.M, mod | KeyModifiers.Shift), vm => vm.Details.TogglePiiPreviewCommand, inTextBox: true);
        Add(WriteMenuItem, new KeyGesture(Key.G, mod), vm => vm.Details.WriteMainCommand, inTextBox: true);
        Add(VariationsMenuItem, new KeyGesture(Key.G, mod | KeyModifiers.Shift), vm => vm.GenerateVariationsCommand, inTextBox: true);
        Add(JudgeMenuItem, new KeyGesture(Key.J, mod), vm => vm.JudgeCommand, inTextBox: true);
        Add(DeleteMenuItem, new KeyGesture(Key.Delete), vm => vm.DeleteSelectionCommand, inTextBox: false, () => RecordListView.IsListFocused);
        FindMenuItem.InputGesture = new KeyGesture(Key.F, mod);

        // Mod+1…5 toggle the tags with those shortcuts, also while typing (the digits stay text without Mod).
        for (var slot = 1; slot <= Domain.Entities.Tag.MaxShortcut; slot++)
        {
            _shortcuts.Add(new Shortcut(new KeyGesture(Key.D0 + slot, mod), vm => vm.ToggleTagSlotCommand, InTextBox: true, Parameter: slot));
            _shortcuts.Add(new Shortcut(new KeyGesture(Key.NumPad0 + slot, mod), vm => vm.ToggleTagSlotCommand, InTextBox: true, Parameter: slot));
        }
    }

    private void Add(MenuItem menuItem, KeyGesture gesture, Func<MainWindowViewModel, ICommand> command, bool inTextBox, Func<bool>? when = null)
    {
        // The menu only shows the gesture; the key itself is handled below so the text-box rule applies.
        menuItem.InputGesture = gesture;
        _shortcuts.Add(new Shortcut(gesture, command, inTextBox, when));
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (FindMenuItem.InputGesture?.Matches(e) == true && viewModel.Records.HasDataset)
        {
            RecordListView.FocusSearch();
            e.Handled = true;
            return;
        }

        // Editor fields (CodeEditor) take the focus in their TextArea.
        var inTextBox = FocusManager?.GetFocusedElement() is TextBox or AvaloniaEdit.Editing.TextArea;
        foreach (var shortcut in _shortcuts)
        {
            if (!shortcut.Gesture.Matches(e) || (inTextBox && !shortcut.InTextBox) || shortcut.When?.Invoke() == false)
            {
                continue;
            }

            var command = shortcut.Command(viewModel);
            if (command.CanExecute(shortcut.Parameter))
            {
                command.Execute(shortcut.Parameter);
            }

            e.Handled = true;
            return;
        }
    }

    private void OnFindClick(object? sender, RoutedEventArgs e) => RecordListView.FocusSearch();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
