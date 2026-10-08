using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
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
        StatusFilters = [new StatusFilterOption(null), .. Enum.GetValues<RecordStatus>().Select(status => new StatusFilterOption(status))];
        RefreshFilterLabels();
        SelectedStatusFilter = StatusFilters[0];
        _localizer.CultureChanged += (_, _) => OnLanguageChanged();
    }

    public ObservableCollection<RecordRowViewModel> Items { get; } = [];

    public IReadOnlyList<StatusFilterOption> StatusFilters { get; }

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

    [ObservableProperty]
    public partial RecordRowViewModel? SelectedItem { get; set; }

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

    private bool IsFiltered => !string.IsNullOrWhiteSpace(SearchText) || SelectedStatusFilter?.Status is not null;

    private RecordFilter Filter => new(
        SelectedStatusFilter?.Status is { } status ? new HashSet<RecordStatus> { status } : null,
        SearchText);

    public async Task ShowDatasetAsync(DatasetNodeViewModel? dataset)
    {
        Dataset = dataset;
        await ReloadAsync();
    }

    /// <summary>
    /// Discards loaded rows and loads the first page for the current dataset and filter.
    /// </summary>
    public async Task ReloadAsync()
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
            var counts = await _queries.CountByStatusAsync(dataset.Id, SearchText);
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
            if (string.IsNullOrWhiteSpace(SearchText))
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

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        _ = ReloadAfterDelayAsync(_searchDelay.Token);
    }

    partial void OnSelectedStatusFilterChanged(StatusFilterOption? value) => _ = ReloadAsync();

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

    private void OnLanguageChanged()
    {
        RefreshFilterLabels();
        foreach (var row in Items)
        {
            row.RefreshLabels();
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmptyDataset));
        OnPropertyChanged(nameof(IsNoMatches));
    }
}
