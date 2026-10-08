using System.Collections.ObjectModel;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

public sealed record StatusFilterOption(RecordStatus? Status, string Label);

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
    private bool _suppressFilterReload;

    public RecordListViewModel(IRecordQueryService queries, StatusBarViewModel status, Localizer localizer)
    {
        _queries = queries;
        _status = status;
        _localizer = localizer;
        StatusFilters = CreateStatusFilters();
        SelectedStatusFilter = StatusFilters[0];
        _localizer.CultureChanged += (_, _) => OnLanguageChanged();
    }

    public ObservableCollection<RecordRowViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<StatusFilterOption> StatusFilters { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataset), nameof(EmptyMessage))]
    public partial DatasetNodeViewModel? Dataset { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// The status filter; <c>null</c> (e.g. while the combo box swaps its items) means all statuses.
    /// </summary>
    [ObservableProperty]
    public partial StatusFilterOption? SelectedStatusFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(EmptyMessage))]
    public partial long TotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
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
    /// Shown over an empty list; <c>null</c> while there are rows (or nothing is selected).
    /// </summary>
    public string? EmptyMessage =>
        Dataset is null || Items.Count > 0 ? null
        : IsLoading ? _localizer["Records_Loading"]
        : IsFiltered ? _localizer["Records_NoMatches"]
        : _localizer["Records_EmptyDataset"];

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
        OnPropertyChanged(nameof(HasMore));

        if (Dataset is not { } dataset)
        {
            OnPropertyChanged(nameof(EmptyMessage));
            return;
        }

        IsLoading = true;
        try
        {
            var filter = Filter;
            var count = await _queries.CountAsync(dataset.Id, filter);
            var page = await _queries.GetPageAsync(dataset.Id, filter, after: null, PageSize);
            if (generation != _generation)
            {
                return; // a newer reload started meanwhile
            }

            TotalCount = count;
            Append(page);
            if (!IsFiltered)
            {
                dataset.RecordCount = count;
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

    public void UpdateStatus(Guid recordId, RecordStatus status)
    {
        if (Items.FirstOrDefault(row => row.Id == recordId) is { } row)
        {
            row.Status = status;
        }
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
        TotalCount = Math.Max(0, TotalCount - 1);
        if (Dataset is { } dataset)
        {
            dataset.RecordCount = Math.Max(0, dataset.RecordCount - 1);
        }

        OnPropertyChanged(nameof(EmptyMessage));
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay?.Cancel();
        _searchDelay = new CancellationTokenSource();
        _ = ReloadAfterDelayAsync(_searchDelay.Token);
    }

    partial void OnSelectedStatusFilterChanged(StatusFilterOption? value)
    {
        if (!_suppressFilterReload)
        {
            _ = ReloadAsync();
        }
    }

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
        OnPropertyChanged(nameof(EmptyMessage));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private List<StatusFilterOption> CreateStatusFilters() =>
    [
        new StatusFilterOption(null, _localizer["Filter_AllStatuses"]),
        .. Enum.GetValues<RecordStatus>().Select(status => new StatusFilterOption(status, Labels.For(_localizer, status))),
    ];

    private void OnLanguageChanged()
    {
        // Swap in translated filter options; the filter itself does not change, so don't reload.
        var selected = SelectedStatusFilter?.Status;
        var filters = CreateStatusFilters();
        _suppressFilterReload = true;
        try
        {
            StatusFilters = filters;
            SelectedStatusFilter = filters.First(option => option.Status == selected);
        }
        finally
        {
            _suppressFilterReload = false;
        }

        foreach (var row in Items)
        {
            row.RefreshLabels();
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyMessage));
    }
}
