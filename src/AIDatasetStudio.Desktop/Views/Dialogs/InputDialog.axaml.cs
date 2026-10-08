using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (InputBox.IsVisible)
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }
        };
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
