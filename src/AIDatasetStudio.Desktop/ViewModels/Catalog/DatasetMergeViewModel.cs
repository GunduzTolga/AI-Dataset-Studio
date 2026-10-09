using System.Collections.ObjectModel;
using AIDatasetStudio.Application.Catalog;
using AIDatasetStudio.Desktop.Localization;
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
/// Datasets › Merge Datasets (M21, SDD §21 V4): datasets of one type, from any projects,
/// combined into a new dataset. The sources are left as they are.
/// </summary>
public sealed partial class DatasetMergeViewModel : ViewModelBase
{
    private readonly IDatasetMergeService _merge;
    private readonly Localizer _localizer;
    private readonly List<MergeSourceItem> _chosen = [];
    private string _suggestedName = string.Empty;
    private CancellationTokenSource? _work;

    public DatasetMergeViewModel(IDatasetMergeService merge, Localizer localizer, IReadOnlyList<ProjectSummary> projects, Guid? preselected = null)
    {
        _merge = merge;
        _localizer = localizer;
        Groups =
        [
            .. projects.Where(p => p.Datasets.Count > 0).Select(p => new MergeProjectGroup(
                p.Name,
                [.. p.Datasets.Select(d => new MergeSourceItem(d, $"{Labels.For(localizer, d.Type)} · {localizer.Format("Common_RecordCount", d.RecordCount)}", this))])),
        ];
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

    public IReadOnlyList<MergeProjectGroup> Groups { get; }

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
        0 => _localizer["Merge_ChooseDatasets"],
        1 => _localizer.Format("Merge_ChooseAnother", Labels.For(_localizer, _chosen[0].Type)),
        _ => _localizer.Format("Merge_Summary", _chosen.Count, _chosen.Sum(c => c.RecordCount), Labels.For(_localizer, _chosen[0].Type)),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
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

    private bool CanMerge() => _chosen.Count >= 2 && !string.IsNullOrWhiteSpace(Name) && SelectedTarget is not null && !IsMerging && Result is null;

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
                new DatasetMergeRequest([.. _chosen.Select(c => c.Id)], target.ProjectId, Name.Trim(), SkipDuplicates, IncludeArchived, AsDrafts),
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
}
