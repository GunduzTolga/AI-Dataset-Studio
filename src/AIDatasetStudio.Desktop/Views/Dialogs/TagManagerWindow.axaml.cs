using AIDatasetStudio.Desktop.ViewModels.Tags;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class TagManagerWindow : Window
{
    public TagManagerWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is TagManagerViewModel tags)
            {
                tags.CloseRequested += (_, _) => Close();
                NewNameBox.Focus();
                await tags.InitializeAsync();
            }
        };
    }
}
