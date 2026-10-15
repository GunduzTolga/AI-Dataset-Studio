using AIDatasetStudio.Desktop.ViewModels.Training;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class TrainingWindow : Window
{
    public TrainingWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is TrainingViewModel training)
            {
                training.CloseRequested += (_, _) => Close();
                training.PropertyChanged += (_, e) =>
                {
                    // The log follows the newest line.
                    if (e.PropertyName is nameof(TrainingViewModel.LogText) or nameof(TrainingViewModel.ShowLog) && training.ShowLog)
                    {
                        Dispatcher.UIThread.Post(() => LogBox.CaretIndex = LogBox.Text?.Length ?? 0, DispatcherPriority.Background);
                    }
                };
            }
        };

        // Hours of training are not lost to a stray click: closing asks first while it runs.
        Closing += (_, e) =>
        {
            if (DataContext is TrainingViewModel training && !training.TryClose())
            {
                e.Cancel = true;
            }
        };
        Closed += (_, _) => (DataContext as TrainingViewModel)?.Dispose();
    }
}
