using AIDatasetStudio.Desktop.ViewModels.Dialogs;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AboutViewModel about)
            {
                about.CloseRequested += (_, _) => Close();
            }
        };
    }
}
