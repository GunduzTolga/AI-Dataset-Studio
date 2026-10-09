using AIDatasetStudio.Desktop.ViewModels.AI;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class VariationsWindow : Window
{
    private bool _canClose;

    public VariationsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is VariationsViewModel variations)
            {
                variations.CloseRequested += (_, _) =>
                {
                    _canClose = true;
                    Close();
                };
            }
        };

        Closed += (_, _) => (DataContext as VariationsViewModel)?.Dispose();

        // Closing the window keeps the variants not added unless the user lets them go.
        Closing += async (_, e) =>
        {
            if (_canClose || DataContext is not VariationsViewModel variations)
            {
                return;
            }

            e.Cancel = true;
            if (await variations.CanCloseAsync())
            {
                _canClose = true;
                Close();
            }
        };
    }
}
