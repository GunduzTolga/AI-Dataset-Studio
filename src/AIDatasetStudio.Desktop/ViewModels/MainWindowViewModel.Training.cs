using AIDatasetStudio.Desktop.ViewModels.Training;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// Training a model with the tool installed on this computer (M28, SDD §21 V5).
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Tools › Train a Model: a folder an export wrote with a training configuration. The tool and
    /// the data are outside the workspace, so none needs to be open.
    /// </summary>
    [RelayCommand]
    private async Task TrainModelAsync()
    {
        if (await _files.PickTrainingFolderAsync() is not { } folder)
        {
            return;
        }

        if (_training.Detect(folder) is not { } framework)
        {
            Status.Set("Status_NoTrainingConfig", Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            return;
        }

        await TrainAsync(new TrainingRunTarget(folder, framework, Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
    }

    private async Task TrainAsync(TrainingRunTarget target)
    {
        using var training = _training.Create(target);
        await _dialogs.ShowTrainingAsync(training);

        // Closing the window stops the run; what it ended with goes to the status bar.
        await training.PendingWork;
        switch (training.State)
        {
            case TrainingState.Succeeded:
                Status.Set("Status_TrainingDone", target.Name, training.OutputDirectory ?? target.Folder);
                break;
            case TrainingState.Failed:
                Status.Set("Status_TrainingFailed", target.Name);
                break;
            case TrainingState.Stopped:
                Status.Set("Status_TrainingStopped", target.Name);
                break;
        }
    }
}
