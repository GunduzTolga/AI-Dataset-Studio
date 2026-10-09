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
    }
}
