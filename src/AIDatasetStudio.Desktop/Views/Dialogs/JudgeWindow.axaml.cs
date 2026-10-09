using AIDatasetStudio.Desktop.ViewModels.AI;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class JudgeWindow : Window
{
    public JudgeWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is JudgeViewModel judge)
            {
                judge.CloseRequested += (_, _) => Close();
            }
        };

        // Judgments are kept as they come: closing only stops the model.
        Closed += (_, _) => (DataContext as JudgeViewModel)?.Dispose();
    }
}
