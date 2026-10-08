using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using Avalonia.Controls.Selection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// A status filter chip: "All" (<see cref="Status"/> is <c>null</c>) or one status, with its record count.
/// </summary>
public sealed partial class StatusFilterOption(RecordStatus? status) : ViewModelBase
{
    public RecordStatus? Status { get; } = status;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial long Count { get; set; }

    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// An entry of the quality filter (analysis results).
/// </summary>
public sealed partial class QualityFilterOption(QualityFilter filter) : ViewModelBase
{
    public QualityFilter Filter { get; } = filter;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;
}

/// <summary>
/// The records of the selected dataset, loaded page by page as the list scrolls (data
/// virtualization on top of the list control's UI virtualization).
/// </summary>
public sealed partial class RecordListViewModel : ViewModelBase
{
    public const int PageSize = 200;

    private readonly IRecordQueryService _queries;
    private readonly StatusBarViewModel _status;
    private readonly Localizer _localizer;
    private RecordCursor? _next;
    private int _generation;
    private CancellationTokenSource? _searchDelay;

    public RecordListViewModel(IRecordQueryService queries, StatusBarViewModel status, Localizer localizer)
    {
        _queries = queries;
        _status = status;
        _localizer = localizer;
        // First: choosing the default filters below reloads the (empty) list, which clears the selection.
        Selection = new SelectionModel<RecordRowViewModel>(Items) { SingleSelect = false };
        Selection.SelectionChanged += (_, _) => OnSelectionChanged();
        StatusFilters = [new StatusFilterOption(null), .. Enum.GetValues<RecordStatus>().Select(status => new StatusFilterOption(status))];
        RefreshFilterLabels();
        SelectedStatusFilter = StatusFilters[0];
        QualityFilters = Enum.GetValues<QualityFilter>().Select(filter => new QualityFilterOption(filter)).ToList();
        RefreshQualityLabels();
        SelectedQualityFilter = QualityFilters[0];
        _localizer.CultureChanged += (_, _) => OnLanguageChanged();
    }

    /// <summary>
    /// Raised when the selected rows change (click, keyboard, reload or code).
    /// </summary>
    public event EventHandler? SelectionChanged;

    public ObservableCollection<RecordRowViewModel> Items { get; } = [];

    /// <summary>
    /// The selected rows; several can be selected with Ctrl/Shift-click for batch actions.
    /// </summary>
    public SelectionModel<RecordRowViewModel> Selection { get; }

    /// <summary>
    /// The first selected row, or <c>null</c>. Setting it selects only that row.
    /// </summary>
    public RecordRowViewModel? SelectedItem
    {
        get => Selection.SelectedItem;
        set
        {
            if (value is null)
            {
                Selection.Clear();
                return;
            }

            var index = Items.IndexOf(value);
            if (index >= 0 && !(Selection.Count == 1 && Selection.SelectedIndex == index))
            {
                using (Selection.BatchUpdate())
                {
                    Selection.Clear();
                    Selection.Select(index);
                }
            }
        }
    }

    public IReadOnlyList<RecordRowViewModel> SelectedRows => Selection.SelectedItems.OfType<RecordRowViewModel>().ToList();

    public int SelectionCount => Selection.Count;

    /// <summary>
    /// The latest reload; awaited when the dataset changes as part of another operation.
    /// </summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<StatusFilterOption> StatusFilters { get; }

    public IReadOnlyList<QualityFilterOption> QualityFilters { get; }

    /// <summary>
    /// Narrows the list by analysis results; <c>null</c> is treated as "all records".
    /// </summary>
    [ObservableProperty]
    public partial QualityFilterOption? SelectedQualityFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataset), nameof(IsEmptyDataset), nameof(IsNoMatches))]
    public partial DatasetNodeViewModel? Dataset { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// The selected chip; <c>null</c> is treated as "all statuses".
    /// </summary>
    [ObservableProperty]
    public partial StatusFilterOption? SelectedStatusFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmptyDataset), nameof(IsNoMatches))]
    public partial long TotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyDataset), nameof(IsNoMatches))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>
    /// How long typing must pause before the search runs.
    /// </summary>
    public TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public bool HasDataset => Dataset is not null;

    public bool HasMore => _next is not null;

    public string CountText => _localizer.Format("Common_RecordCount", TotalCount);

    /// <summary>
    /// The dataset has no records at all (not merely none matching the filter).
    /// </summary>
    public bool IsEmptyDataset => Dataset is not null && Items.Count == 0 && !IsLoading && !IsFiltered;

    /// <summary>
    /// Records exist but none match the search or status filter.
    /// </summary>
    public bool IsNoMatches => Dataset is not null && Items.Count == 0 && !IsLoading && IsFiltered;

    private bool IsFiltered => !string.IsNullOrWhiteSpace(SearchText) || SelectedStatusFilter?.Status is not null || Quality != QualityFilter.None;

    private QualityFilter Quality => SelectedQualityFilter?.Filter ?? QualityFilter.None;

    private RecordFilter Filter => new(
        SelectedStatusFilter?.Status is { } status ? new HashSet<RecordStatus> { status } : null,
        SearchText,
        Quality);

    public async Task ShowDatasetAsync(DatasetNodeViewModel? dataset)
    {
        Dataset = dataset;
        await ReloadAsync();
    }

    /// <summary>
    /// Discards loaded rows and loads the first page for the current dataset and filter.
    /// </summary>
    public Task ReloadAsync() => Loading = ReloadCoreAsync();

    /// <summary>
    /// Selects the row after the current one, loading the next page when needed.
    /// </summary>
    /// <returns><c>false</c> at the end of the list.</returns>
    public async Task<bool> SelectNextAsync()
    {
        var index = SelectedItem is { } current ? Items.IndexOf(current) + 1 : 0;
        if (index >= Items.Count && HasMore)
        {
            await LoadMoreAsync();
        }

        if (index >= Items.Count)
        {
            return false;
        }

        SelectedItem = Items[index];
        return true;
    }

    /// <returns><c>false</c> at the start of the list.</returns>
    public bool SelectPrevious()
    {
        var index = SelectedItem is { } current ? Items.IndexOf(current) - 1 : -1;
        if (index < 0)
        {
            return false;
        }

        SelectedItem = Items[index];
        return true;
    }

    /// <summary>
    /// Selects the loaded rows with these ids.
    /// </summary>
    /// <returns><c>true</c> if at least one is loaded.</returns>
    public bool Select(IReadOnlyCollection<Guid> recordIds)
    {
        var indexes = Items.Select((row, index) => (row, index)).Where(x => recordIds.Contains(x.row.Id)).Select(x => x.index).ToList();
        using (Selection.BatchUpdate())
        {
            Selection.Clear();
            foreach (var index in indexes)
            {
                Selection.Select(index);
            }
        }

        return indexes.Count > 0;
    }

    /// <summary>
    /// Shows new analysis results (tokens, scores) for the loaded rows without reloading the list.
    /// With a quality filter active the rows that match may change, so the list is reloaded instead.
    /// </summary>
    public async Task RefreshAnalysisAsync(IReadOnlyCollection<Guid>? recordIds = null)
    {
        if (Dataset is null)
        {
            return;
        }

        if (Quality != QualityFilter.None && recordIds is null)
        {
            await ReloadAsync();
            return;
        }

        var rows = Items.Where(row => recordIds is null || recordIds.Contains(row.Id)).ToDictionary(row => row.Id);
        try
        {
            foreach (var chunk in rows.Keys.Chunk(PageSize))
            {
                foreach (var summary in await _queries.GetSummariesAsync(chunk))
                {
                    if (rows.TryGetValue(summary.Id, out var row))
                    {
                        row.Apply(summary);
                    }
                }
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    /// <summary>
    /// Shows the records with a quality issue (from the dataset overview).
    /// </summary>
    public void ShowQuality(QualityFilter filter)
    {
        SelectedStatusFilter = StatusFilters[0];
        SelectedQualityFilter = QualityFilters.First(option => option.Filter == filter);
    }

    /// <summary>
    /// Reflects edited content made elsewhere.
    /// </summary>
    public void UpdateRow(Guid recordId, string preview, RecordStatus status)
    {
        if (Items.FirstOrDefault(row => row.Id == recordId) is { } row)
        {
            row.Preview = preview;
            UpdateStatus(recordId, status);
        }
    }

    private async Task ReloadCoreAsync()
    {
        var generation = ++_generation;
        _next = null;
        SelectedItem = null;
        Items.Clear();
        TotalCount = 0;
        foreach (var option in StatusFilters)
        {
            option.Count = 0;
        }

        OnPropertyChanged(nameof(HasMore));

        if (Dataset is not { } dataset)
        {
            OnPropertyChanged(nameof(IsEmptyDataset));
            OnPropertyChanged(nameof(IsNoMatches));
            return;
        }

        IsLoading = true;
        try
        {
            var counts = await _queries.CountByStatusAsync(dataset.Id, SearchText, Quality);
            var page = await _queries.GetPageAsync(dataset.Id, Filter, after: null, PageSize);
            if (generation != _generation)
            {
                return; // a newer reload started meanwhile
            }

            foreach (var option in StatusFilters)
            {
                option.Count = option.Status is { } status ? counts[status] : counts.Values.Sum();
            }

            UpdateTotal();
            Append(page);
            if (string.IsNullOrWhiteSpace(SearchText) && Quality == QualityFilter.None)
            {
                dataset.RecordCount = counts.Values.Sum();
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    private bool CanLoadMore() => HasMore && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (Dataset is not { } dataset || _next is not { } cursor)
        {
            return;
        }

        var generation = _generation;
        IsLoading = true;
        try
        {
            var page = await _queries.GetPageAsync(dataset.Id, Filter, cursor, PageSize);
            if (generation == _generation)
            {
                Append(page);
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Reflects a status change made elsewhere. The row stays in place until the next reload,
    /// even if it no longer matches the filter, so the selection does not jump.
    /// </summary>
    public void UpdateStatus(Guid recordId, RecordStatus status)
    {
        if (Items.FirstOrDefault(row => row.Id == recordId) is not { } row || row.Status == status)
        {
            return;
        }

        Option(row.Status).Count--;
        Option(status).Count++;
        row.Status = status;
        UpdateTotal();
    }

    /// <summary>
    /// Drops a deleted record from the list. The dataset total shrinks too, whatever the filter.
    /// </summary>
    public void Remove(Guid recordId)
    {
        if (Items.FirstOrDefault(row => row.Id == recordId) is not { } row)
        {
            return;
        }

        Items.Remove(row);
        Option(row.Status).Count--;
        StatusFilters[0].Count--;
        UpdateTotal();
        if (Dataset is { } dataset)
        {
            dataset.RecordCount = Math.Max(0, dataset.RecordCount - 1);
        }

        OnPropertyChanged(nameof(IsEmptyDataset));

        OnPropertyChanged(nameof(IsNoMatches));
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(SelectionCount));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        _ = ReloadAfterDelayAsync(_searchDelay.Token);
    }

    partial void OnSelectedStatusFilterChanged(StatusFilterOption? value) => _ = ReloadAsync();

    partial void OnSelectedQualityFilterChanged(QualityFilterOption? value) => _ = ReloadAsync();

    private async Task ReloadAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by further typing
        }

        await ReloadAsync();
    }

    private void Append(RecordPage page)
    {
        foreach (var summary in page.Items)
        {
            Items.Add(new RecordRowViewModel(summary, _localizer));
        }

        _next = page.Next;
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(IsEmptyDataset));
        OnPropertyChanged(nameof(IsNoMatches));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private StatusFilterOption Option(RecordStatus status) => StatusFilters.First(option => option.Status == status);

    private void UpdateTotal() => TotalCount = (SelectedStatusFilter ?? StatusFilters[0]).Count;

    private void RefreshFilterLabels()
    {
        foreach (var option in StatusFilters)
        {
            option.Label = option.Status is { } status ? Labels.For(_localizer, status) : _localizer["Filter_AllStatuses"];
        }
    }

    private void RefreshQualityLabels()
    {
        foreach (var option in QualityFilters)
        {
            option.Label = _localizer[$"QualityFilter_{option.Filter}"];
        }
    }

    private void OnLanguageChanged()
    {
        RefreshFilterLabels();
        RefreshQualityLabels();
        foreach (var row in Items)
        {
            row.RefreshLabels();
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmptyDataset));
        OnPropertyChanged(nameof(IsNoMatches));
    }
}
