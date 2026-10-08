using System.Collections.ObjectModel;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.ViewModels;

public abstract partial class ExplorerNodeViewModel(Guid id, string name) : ViewModelBase
{
    public Guid Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

public sealed class ProjectNodeViewModel(Guid id, string name) : ExplorerNodeViewModel(id, name)
{
    public ObservableCollection<DatasetNodeViewModel> Datasets { get; } = [];
}

public sealed partial class DatasetNodeViewModel : ExplorerNodeViewModel
{
    private readonly Localizer _localizer;

    public DatasetNodeViewModel(Guid id, Guid projectId, string name, DatasetType type, long recordCount, Localizer localizer)
        : base(id, name)
    {
        ProjectId = projectId;
        Type = type;
        RecordCount = recordCount;
        _localizer = localizer;
    }

    public Guid ProjectId { get; }

    public DatasetType Type { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    public partial long RecordCount { get; set; }

    /// <summary>
    /// "Chat · 1,234 records".
    /// </summary>
    public string Details => $"{Labels.For(_localizer, Type)} · {_localizer.Format("Common_RecordCount", RecordCount)}";

    public void RefreshLabels() => OnPropertyChanged(nameof(Details));
}
