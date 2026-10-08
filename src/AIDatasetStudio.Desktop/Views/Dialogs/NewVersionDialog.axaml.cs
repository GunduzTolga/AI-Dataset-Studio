using AIDatasetStudio.Desktop.ViewModels.Versions;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class NewVersionDialog : Window
{
    public NewVersionDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is NewVersionViewModel prompt)
            {
                prompt.CloseRequested += (_, create) => Close(create);
            }

            NumberBox.Focus();
            NumberBox.SelectAll();
        };
    }
}
