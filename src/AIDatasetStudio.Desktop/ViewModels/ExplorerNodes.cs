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

    public DatasetNodeViewModel(Guid id, Guid projectId, string projectName, string name, DatasetType type, long recordCount, Localizer localizer)
        : base(id, name)
    {
        ProjectId = projectId;
        ProjectName = projectName;
        Type = type;
        RecordCount = recordCount;
        _localizer = localizer;
    }

    public Guid ProjectId { get; }

    /// <summary>
    /// Follows renames of the project.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string ProjectName { get; set; }

    public DatasetType Type { get; }

    /// <summary>
    /// Tokenizer for token counts, e.g. "o200k_base".
    /// </summary>
    [ObservableProperty]
    public partial string Tokenizer { get; set; } = Domain.Entities.Dataset.DefaultTokenizerName;

    /// <summary>
    /// Records with more tokens than this are flagged.
    /// </summary>
    [ObservableProperty]
    public partial int ContextLength { get; set; } = Domain.Entities.Dataset.DefaultContextLength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(Subtitle))]
    public partial long RecordCount { get; set; }

    /// <summary>
    /// The newest version, e.g. "1.2.0"; <c>null</c> while the dataset has none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string? LatestVersion { get; set; }

    public string TypeLabel => Labels.For(_localizer, Type);

    /// <summary>
    /// Compact count for the tree, e.g. "1.234".
    /// </summary>
    public string CountText => RecordCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// "Project · 1,234 records · v1.2.0" under the dataset title.
    /// </summary>
    public string Subtitle => LatestVersion is null
        ? $"{ProjectName} · {_localizer.Format("Common_RecordCount", RecordCount)}"
        : $"{ProjectName} · {_localizer.Format("Common_RecordCount", RecordCount)} · v{LatestVersion}";

    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(Subtitle));
    }
}
