using AIDatasetStudio.Desktop.ViewModels.AI;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class AIConnectionsWindow : Window
{
    public AIConnectionsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is AIConnectionsViewModel connections)
            {
                connections.CloseRequested += (_, _) => Close();
            }
        };

        // A key typed just before closing is kept.
        Closed += (_, _) => (DataContext as AIConnectionsViewModel)?.SavePendingKeys();
    }
}
