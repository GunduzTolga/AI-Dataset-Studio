using System.Collections.ObjectModel;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// The project / dataset tree of the open workspace.
/// </summary>
public sealed partial class ExplorerViewModel : ViewModelBase
{
    private readonly ICatalogService _catalog;
    private readonly IDatasetMergeService _merge;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _files;
    private readonly StatusBarViewModel _status;
    private readonly Localizer _localizer;

    public ExplorerViewModel(ICatalogService catalog, IDatasetMergeService merge, IDialogService dialogs, IFileDialogService files, StatusBarViewModel status, Localizer localizer)
    {
        _catalog = catalog;
        _merge = merge;
        _dialogs = dialogs;
        _files = files;
        _status = status;
        _localizer = localizer;
        _localizer.CultureChanged += (_, _) =>
        {
            foreach (var dataset in AllDatasets())
            {
                dataset.RefreshLabels();
            }
        };
    }

    public ObservableCollection<ProjectNodeViewModel> Projects { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDataset), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(NewProjectCommand), nameof(NewDatasetCommand), nameof(RenameCommand), nameof(DeleteCommand), nameof(MergeDatasetsCommand))]
    public partial bool IsWorkspaceOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDataset))]
    [NotifyCanExecuteChangedFor(nameof(NewDatasetCommand), nameof(RenameCommand), nameof(DeleteCommand))]
    public partial ExplorerNodeViewModel? SelectedNode { get; set; }

    public DatasetNodeViewModel? SelectedDataset => SelectedNode as DatasetNodeViewModel;

    public bool IsEmpty => IsWorkspaceOpen && Projects.Count == 0;

    /// <summary>
    /// Rebuilds the tree from the workspace, keeping the selection and expanded projects.
    /// </summary>
    public async Task ReloadAsync()
    {
        var selectedId = SelectedNode?.Id;
        var expanded = Projects.Where(p => p.IsExpanded).Select(p => p.Id).ToHashSet();
        var projects = await _catalog.GetProjectsAsync();

        Projects.Clear();
        foreach (var project in projects)
        {
            var node = new ProjectNodeViewModel(project.Id, project.Name) { IsExpanded = expanded.Contains(project.Id) || project.Id == selectedId };
            foreach (var dataset in project.Datasets)
            {
                node.Datasets.Add(new DatasetNodeViewModel(dataset.Id, dataset.ProjectId, project.Name, dataset.Name, dataset.Type, dataset.RecordCount, _localizer)
                {
                    Tokenizer = dataset.Tokenizer,
                    ContextLength = dataset.ContextLength,
                    AIGuidance = dataset.AIGuidance,
                    LatestVersion = dataset.LatestVersion,
                });
                if (dataset.Id == selectedId)
                {
                    node.IsExpanded = true;
                }
            }

            Projects.Add(node);
        }

        SelectedNode = FindNode(selectedId);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Merges datasets of one type from any projects into a new dataset, which is then shown (M21).
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsWorkspaceOpen))]
    private async Task MergeDatasetsAsync()
    {
        try
        {
            await using var merge = new Catalog.DatasetMergeViewModel(_merge, _files, _localizer, await _catalog.GetProjectsAsync(), SelectedDataset?.Id);
            await _dialogs.ShowDatasetMergeAsync(merge);
            if (merge.Result is { } result)
            {
                await ReloadAsync();
                SelectedNode = FindNode(result.DatasetId);
                _status.Set("Status_Merged", merge.Name.Trim(), result.Copied);
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    public void Clear()
    {
        SelectedNode = null;
        Projects.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public DatasetNodeViewModel? FindDataset(Guid datasetId) => AllDatasets().FirstOrDefault(d => d.Id == datasetId);

    public void SetRecordCount(Guid datasetId, long count)
    {
        if (AllDatasets().FirstOrDefault(d => d.Id == datasetId) is { } dataset)
        {
            dataset.RecordCount = count;
        }
    }

    [RelayCommand(CanExecute = nameof(IsWorkspaceOpen))]
    private async Task NewProjectAsync()
    {
        var name = await _dialogs.PromptTextAsync(_localizer["Dialog_NewProjectTitle"], _localizer["Dialog_NewProjectMessage"], _localizer["Default_ProjectName"]);
        if (name is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var id = await _catalog.CreateProjectAsync(name);
            await ReloadAsync();
            SelectedNode = FindNode(id);
            _status.Set("Status_ProjectCreated", name);
        });
    }

    private bool CanNewDataset() => IsWorkspaceOpen && SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(CanNewDataset))]
    private async Task NewDatasetAsync()
    {
        var projectId = SelectedNode switch
        {
            ProjectNodeViewModel project => project.Id,
            DatasetNodeViewModel dataset => dataset.ProjectId,
            _ => (Guid?)null,
        };
        if (projectId is null || await _dialogs.PromptNewDatasetAsync() is not { } request)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var id = await _catalog.CreateDatasetAsync(projectId.Value, request.Name, request.Type);
            await ReloadAsync();
            SelectedNode = FindNode(id);
            _status.Set("Status_DatasetCreated", request.Name);
        });
    }

    private bool HasSelection() => IsWorkspaceOpen && SelectedNode is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameAsync()
    {
        if (SelectedNode is not { } node
            || await _dialogs.PromptTextAsync(_localizer["Dialog_RenameTitle"], _localizer["Dialog_RenameMessage"], node.Name) is not { } name
            || name == node.Name)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (node is ProjectNodeViewModel project)
            {
                await _catalog.RenameProjectAsync(node.Id, name);
                foreach (var dataset in project.Datasets)
                {
                    dataset.ProjectName = name; // shown in the dataset's subtitle
                }
            }
            else
            {
                await _catalog.RenameDatasetAsync(node.Id, name);
            }

            node.Name = name;
            _status.Set("Status_Renamed", name);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        var confirmed = node switch
        {
            ProjectNodeViewModel => await _dialogs.ConfirmAsync(
                _localizer["Dialog_DeleteProjectTitle"],
                _localizer.Format("Dialog_DeleteProjectMessage", node.Name),
                _localizer["Action_Delete"],
                isDestructive: true),
            DatasetNodeViewModel dataset => await _dialogs.ConfirmAsync(
                _localizer["Dialog_DeleteDatasetTitle"],
                _localizer.Format("Dialog_DeleteDatasetMessage", node.Name, dataset.RecordCount),
                _localizer["Action_Delete"],
                isDestructive: true),
            _ => false,
        };
        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (node is ProjectNodeViewModel)
            {
                await _catalog.DeleteProjectAsync(node.Id);
            }
            else
            {
                await _catalog.DeleteDatasetAsync(node.Id);
            }

            SelectedNode = null;
            await ReloadAsync();
            _status.Set("Status_Deleted", node.Name);
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
            await ReloadAsync();
        }
    }

    private ExplorerNodeViewModel? FindNode(Guid? id) =>
        id is null
            ? null
            : Projects.Cast<ExplorerNodeViewModel>().Concat(AllDatasets()).FirstOrDefault(node => node.Id == id);

    private IEnumerable<DatasetNodeViewModel> AllDatasets() => Projects.SelectMany(p => p.Datasets);
}
