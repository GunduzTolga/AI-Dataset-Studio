using AIDatasetStudio.Desktop.ViewModels.Import;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class HubImportWindow : Window
{
    public HubImportWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is HubImportViewModel hub)
            {
                hub.CloseRequested += (_, _) => Close();
            }

            DatasetBox.Focus();
        };

        Closing += (_, e) =>
        {
            // Closing from the title bar mid-download: stop it (nothing is left behind), then close.
            if (DataContext is HubImportViewModel { IsBusy: true } hub)
            {
                e.Cancel = true;
                hub.CancelCommand.Execute(null);
                _ = hub.PendingWork.ContinueWith(_ => Close(), TaskScheduler.FromCurrentSynchronizationContext());
            }
        };
    }
}
