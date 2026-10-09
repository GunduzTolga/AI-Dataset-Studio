using System.Collections.ObjectModel;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Catalog;

/// <summary>
/// A dataset that can be merged, under its project.
/// </summary>
public sealed partial class MergeSourceItem : ViewModelBase
{
    private readonly DatasetMergeViewModel _owner;

    internal MergeSourceItem(DatasetSummary dataset, string detail, DatasetMergeViewModel owner)
    {
        _owner = owner;
        Id = dataset.Id;
        Name = dataset.Name;
        Type = dataset.Type;
        RecordCount = dataset.RecordCount;
        Detail = detail;
    }

    public Guid Id { get; }

    public string Name { get; }

    public DatasetType Type { get; }

    public long RecordCount { get; }

    /// <summary>
    /// "Instruction · 120 records".
    /// </summary>
    public string Detail { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>
    /// Only datasets of the type of the first chosen one can join it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; internal set; } = true;

    partial void OnIsCheckedChanged(bool value) => _owner.OnChosenChanged(this, value);
}

public sealed record MergeProjectGroup(string Name, IReadOnlyList<MergeSourceItem> Datasets);

public sealed record MergeTargetOption(Guid ProjectId, string Name);

/// <summary>
/// Datasets › Merge Datasets (M21, SDD §21 V4): datasets of one type, from any projects of this
/// workspace or of another workspace file (M23), combined into a new dataset. The sources are
/// left as they are. Dispose it once the window is closed: a file chosen is read from a copy.
/// </summary>
public sealed partial class DatasetMergeViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IDatasetMergeService _merge;
    private readonly IFileDialogService _files;
    private readonly Localizer _localizer;
    private readonly IReadOnlyList<ProjectSummary> _projects;
    private readonly List<MergeSourceItem> _chosen = [];
    private string _suggestedName = string.Empty;
    private CancellationTokenSource? _work;
    private IMergeSource? _source;
    private bool _picking;
    private bool _disposed;

    public DatasetMergeViewModel(IDatasetMergeService merge, IFileDialogService files, Localizer localizer, IReadOnlyList<ProjectSummary> projects, Guid? preselected = null)
    {
        _merge = merge;
        _files = files;
        _localizer = localizer;
        _projects = projects;
        Groups = GroupsOf(projects);
        Targets = [.. projects.Select(p => new MergeTargetOption(p.Id, p.Name))];
        SelectedTarget = Targets.FirstOrDefault(t => projects.Any(p => p.Id == t.ProjectId && p.Datasets.Any(d => d.Id == preselected))) ?? Targets.FirstOrDefault();
        Name = string.Empty;
        if (Sources.FirstOrDefault(s => s.Id == preselected) is { } first)
        {
            first.IsChecked = true;
        }
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// The datasets to choose from: this workspace's, or the chosen file's.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sources), nameof(HasNoSources))]
    public partial IReadOnlyList<MergeProjectGroup> Groups { get; private set; }

    public bool HasNoSources => Groups.Count == 0;

    /// <summary>
    /// The other workspace file the datasets come from, as "Destek.adstudio"; <c>null</c> for this workspace.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFromOtherFile), nameof(SourceText))]
    [NotifyCanExecuteChangedFor(nameof(UseThisWorkspaceCommand))]
    public partial string? SourceFileName { get; private set; }

    public bool IsFromOtherFile => SourceFileName is not null;

    /// <summary>
    /// "From this workspace", or "From Destek.adstudio".
    /// </summary>
    public string SourceText => SourceFileName is { } file ? _localizer.Format("Merge_FromOtherFile", file) : _localizer["Merge_FromThisWorkspace"];

    /// <summary>
    /// A chosen file is being read.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand), nameof(UseThisWorkspaceCommand))]
    public partial bool IsOpeningFile { get; private set; }

    public IEnumerable<MergeSourceItem> Sources => Groups.SelectMany(g => g.Datasets);

    /// <summary>
    /// The chosen datasets in the order they were chosen: the first one's settings are taken.
    /// </summary>
    public IReadOnlyList<MergeSourceItem> Chosen => _chosen;

    public IReadOnlyList<MergeTargetOption> Targets { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    public partial MergeTargetOption? SelectedTarget { get; set; }

    /// <summary>
    /// The new dataset's name; follows the chosen datasets until the user types one.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool SkipDuplicates { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeArchived { get; set; }

    [ObservableProperty]
    public partial bool AsDrafts { get; set; }

    /// <summary>
    /// "3 datasets · 1,240 records (Instruction)", or what is missing.
    /// </summary>
    public string SummaryText => _chosen.Count switch
    {
        0 => _localizer[IsFromOtherFile ? "Merge_ChooseFromFile" : "Merge_ChooseDatasets"],
        1 when !IsFromOtherFile => _localizer.Format("Merge_ChooseAnother", Labels.For(_localizer, _chosen[0].Type)),
        1 => _localizer.Format("Merge_SummaryOne", _chosen[0].RecordCount, Labels.For(_localizer, _chosen[0].Type)),
        _ => _localizer.Format("Merge_Summary", _chosen.Count, _chosen.Sum(c => c.RecordCount), Labels.For(_localizer, _chosen[0].Type)),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand), nameof(ChooseFileCommand), nameof(UseThisWorkspaceCommand))]
    public partial bool IsMerging { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// The finished merge; the window shows what was written.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsSetup), nameof(ResultText))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    public partial DatasetMergeResult? Result { get; private set; }

    public bool IsDone => Result is not null;

    public bool IsSetup => !IsMerging && !IsDone;

    public string ResultText
    {
        get
        {
            if (Result is not { } r)
            {
                return string.Empty;
            }

            var text = _localizer.Format("Merge_Done", Name.Trim(), r.Copied);
            if (r.Duplicates > 0)
            {
                text += " " + _localizer.Format("Merge_DoneDuplicates", r.Duplicates);
            }

            if (r.Archived > 0)
            {
                text += " " + _localizer.Format("Merge_DoneArchived", r.Archived);
            }

            return text;
        }
    }

    /// <summary>
    /// The current merge; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    // From another file one dataset is enough: it is brought over.
    private bool CanMerge() =>
        _chosen.Count >= (IsFromOtherFile ? 1 : 2) && !string.IsNullOrWhiteSpace(Name) && SelectedTarget is not null
        && !IsMerging && !_picking && !IsOpeningFile && Result is null;

    private bool CanChangeSource() => !IsMerging && Result is null;

    /// <summary>
    /// Lists the datasets of another workspace file instead. The file is read from a copy and
    /// never changed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeSource))]
    private async Task ChooseFileAsync()
    {
        // No merge starts while the picker is open: it would read the datasets being replaced.
        string? path;
        SetPicking(true);
        try
        {
            path = await _files.PickWorkspaceToOpenAsync();
        }
        finally
        {
            SetPicking(false);
        }

        if (path is null || IsMerging || _disposed)
        {
            return;
        }

        ErrorMessage = null;
        IsOpeningFile = true;
        try
        {
            var source = await _merge.OpenWorkspaceAsync(path);
            if (_disposed)
            {
                await source.DisposeAsync();
                return;
            }

            await UseSourceAsync(source);
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            ErrorMessage = _localizer.Format(error.Key, error.Args);
        }
        finally
        {
            IsOpeningFile = false;
        }
    }

    private void SetPicking(bool picking)
    {
        _picking = picking;
        MergeCommand.NotifyCanExecuteChanged();
        UseThisWorkspaceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Lists this workspace's datasets again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseThisWorkspace))]
    private Task UseThisWorkspaceAsync()
    {
        ErrorMessage = null;
        return UseSourceAsync(null);
    }

    private bool CanUseThisWorkspace() => IsFromOtherFile && !_picking && !IsOpeningFile && CanChangeSource();

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_source is { } source)
        {
            _source = null;
            await source.DisposeAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private Task MergeAsync()
    {
        PendingWork = RunAsync();
        return PendingWork;
    }

    /// <summary>
    /// Stops a running merge (the new dataset is removed again), or closes the window.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_work is { } work)
        {
            work.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal void OnChosenChanged(MergeSourceItem item, bool chosen)
    {
        _chosen.Remove(item);
        if (chosen)
        {
            _chosen.Add(item);
        }

        RefreshChoice();
    }

    private void RefreshChoice()
    {
        var type = _chosen.FirstOrDefault()?.Type;
        foreach (var source in Sources)
        {
            source.IsEnabled = type is null || source.Type == type;
        }

        // The name follows the choice until the user types their own.
        if (string.IsNullOrWhiteSpace(Name) || Name == _suggestedName)
        {
            _suggestedName = string.Join(" + ", _chosen.Select(c => c.Name));
            if (_suggestedName.Length > 100)
            {
                _suggestedName = _suggestedName[..100].TrimEnd();
            }

            Name = _suggestedName;
        }

        OnPropertyChanged(nameof(SummaryText));
        MergeCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync()
    {
        if (SelectedTarget is not { } target)
        {
            return;
        }

        using var work = new CancellationTokenSource();
        _work = work;
        ErrorMessage = null;
        ProgressValue = 0;
        ProgressText = _localizer.Format("Merge_Progress", 0, _chosen.Sum(c => c.RecordCount));
        IsMerging = true;
        var progress = new Progress<DatasetMergeProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : Math.Min(1, (double)p.Processed / p.Total);
            ProgressText = _localizer.Format("Merge_Progress", p.Processed, p.Total);
        });

        try
        {
            Result = await _merge.MergeAsync(
                new DatasetMergeRequest([.. _chosen.Select(c => c.Id)], target.ProjectId, Name.Trim(), SkipDuplicates, IncludeArchived, AsDrafts, _source),
                progress,
                work.Token);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            ErrorMessage = _localizer["Merge_Cancelled"];
        }
        catch (Exception ex) when (UiErrors.TryDescribe(ex) is { } error)
        {
            ErrorMessage = _localizer.Format(error.Key, error.Args);
        }
        finally
        {
            _work = null;
            IsMerging = false;
        }
    }

    private IReadOnlyList<MergeProjectGroup> GroupsOf(IReadOnlyList<ProjectSummary> projects) =>
    [
        .. projects.Where(p => p.Datasets.Count > 0).Select(p => new MergeProjectGroup(
            p.Name,
            [.. p.Datasets.Select(d => new MergeSourceItem(d, $"{Labels.For(_localizer, d.Type)} · {_localizer.Format("Common_RecordCount", d.RecordCount)}", this))])),
    ];

    // The choice starts over with the other datasets; a name the user typed stays.
    private async Task UseSourceAsync(IMergeSource? source)
    {
        var previous = _source;
        _source = source;
        _chosen.Clear();
        Groups = GroupsOf(source?.Projects ?? _projects);
        SourceFileName = source?.FileName;
        RefreshChoice();
        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
    }
}
