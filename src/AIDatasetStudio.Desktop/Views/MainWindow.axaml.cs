using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIDatasetStudio.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ConfigureHotKeys();
    }

    // Ctrl on Windows/Linux, Cmd on macOS.
    private void ConfigureHotKeys()
    {
        var modifier = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        NewWorkspaceMenuItem.HotKey = NewWorkspaceMenuItem.InputGesture = new KeyGesture(Key.N, modifier);
        OpenWorkspaceMenuItem.HotKey = OpenWorkspaceMenuItem.InputGesture = new KeyGesture(Key.O, modifier);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
