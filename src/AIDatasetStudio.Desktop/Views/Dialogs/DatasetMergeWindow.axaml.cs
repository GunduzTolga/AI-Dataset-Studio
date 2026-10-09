using AIDatasetStudio.Desktop.ViewModels.Catalog;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class DatasetMergeWindow : Window
{
    public DatasetMergeWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is DatasetMergeViewModel merge)
            {
                merge.CloseRequested += (_, _) => Close();
            }
        };

        Closing += (_, e) =>
        {
            // Closing from the title bar mid-merge: stop it (the new dataset is removed), then close.
            if (DataContext is DatasetMergeViewModel { IsMerging: true } merge)
            {
                e.Cancel = true;
                merge.CancelCommand.Execute(null);
                _ = merge.PendingWork.ContinueWith(_ => Close(), TaskScheduler.FromCurrentSynchronizationContext());
            }
        };
    }
}
