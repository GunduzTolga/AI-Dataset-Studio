using AIDatasetStudio.Desktop.ViewModels.Export;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class ExportWizard : Window
{
    public ExportWizard()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is ExportWizardViewModel wizard)
            {
                wizard.CloseRequested += (_, _) => Close();
                await wizard.InitializeAsync();
            }
        };
        Closing += (_, e) =>
        {
            // Closing from the title bar mid-export: cancel first (nothing is written), close afterwards.
            if (DataContext is ExportWizardViewModel { IsExporting: true } wizard)
            {
                e.Cancel = true;
                wizard.CancelCommand.Execute(null);
            }
        };
    }
}
