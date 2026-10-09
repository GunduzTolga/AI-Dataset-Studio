using AIDatasetStudio.Desktop.ViewModels.Documents;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class QAExtractionWindow : Window
{
    private bool _canClose;

    public QAExtractionWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is QAExtractionViewModel extraction)
            {
                extraction.CloseRequested += (_, _) =>
                {
                    _canClose = true;
                    Close();
                };
                _ = extraction.InitializeAsync();
            }
        };

        Closed += (_, _) => (DataContext as QAExtractionViewModel)?.Dispose();

        // Closing the window keeps the pairs not added unless the user lets them go.
        Closing += async (_, e) =>
        {
            if (_canClose || DataContext is not QAExtractionViewModel extraction)
            {
                return;
            }

            e.Cancel = true;
            if (await extraction.CanCloseAsync())
            {
                _canClose = true;
                Close();
            }
        };
    }
}
