using AIDatasetStudio.Desktop.ViewModels.Versions;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class VersionsWindow : Window
{
    public VersionsWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is VersionsViewModel versions)
            {
                versions.CloseRequested += (_, _) => Close();
                await versions.InitializeAsync();
            }
        };
        Closing += (_, e) =>
        {
            // A version is being created or the dataset is going back: let it finish.
            if (DataContext is VersionsViewModel { IsBusy: true })
            {
                e.Cancel = true;
            }
        };
    }
}
