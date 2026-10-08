using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// AI connections (SDD §17): Tools › AI Connections, also opened from the overview and the editor.
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Tools › AI Connections: the model servers that help write records. They belong to this
    /// computer, so no workspace is needed.
    /// </summary>
    [RelayCommand]
    private Task ManageAIConnectionsAsync() => _dialogs.ShowAIConnectionsAsync(_ai.CreateConnections());
}
