using AIDatasetStudio.Desktop.ViewModels.AI;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class ModelEvaluationWindow : Window
{
    public ModelEvaluationWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is ModelEvaluationViewModel evaluation)
            {
                evaluation.CloseRequested += (_, _) => Close();
            }
        };

        // Closing the window, by its close button too, stops the run.
        Closed += (_, _) => (DataContext as ModelEvaluationViewModel)?.Dispose();
    }
}
