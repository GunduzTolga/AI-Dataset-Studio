using AIDatasetStudio.Desktop.ViewModels.Import;
using Avalonia.Controls;

namespace AIDatasetStudio.Desktop.Views.Dialogs;

public sealed partial class ImportWizard : Window
{
    public ImportWizard()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is ImportWizardViewModel wizard)
            {
                wizard.CloseRequested += (_, _) => Close();
                await wizard.InitializeAsync();
            }
        };
        Closing += (_, e) =>
        {
            // Closing from the title bar mid-import: cancel first (nothing is kept), close afterwards.
            if (DataContext is ImportWizardViewModel { IsImporting: true } wizard)
            {
                e.Cancel = true;
                wizard.CancelCommand.Execute(null);
            }
        };
    }
}
