using System.Collections.ObjectModel;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Versions;

/// <summary>
/// The versions of a dataset (SDD §14): the working copy and the frozen versions, newest first.
/// The selected one is compared with another (by default the one before it): which records were
/// added, removed or changed, and for a selected record its fields and tags side by side with the
/// differences marked. A version can be exported, gone back to or deleted; the working copy can
/// be frozen as a new version.
/// </summary>
public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly IVersionService _versions;
    private readonly RecordOperations _operations;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private readonly TagCatalog _tags;
    private readonly Func<VersionInfo, Task>? _export;
    private CancellationTokenSource? _work;
    private IReadOnlyList<ChangeItemViewModel> _allChanges = [];
    private long _recordCount;
    private bool _selecting;

    /// <param name="versions">The version service.</param>
    /// <param name="operations">Records the rollback in the undo history.</param>
    /// <param name="dialogs">Questions and the "new version" dialog.</param>
    /// <param name="localizer">Texts.</param>
    /// <param name="tags">Names and colors of the tags.</param>
    /// <param name="target">The dataset.</param>
    /// <param name="export">Opens the export wizard for a version.</param>
    public VersionsViewModel(
        IVersionService versions,
        RecordOperations operations,
        IDialogService dialogs,
        Localizer localizer,
        TagCatalog tags,
        VersionsTarget target,
        Func<VersionInfo, Task>? export = null)
    {
        _versions = versions;
        _operations = operations;
        _dialogs = dialogs;
        _localizer = localizer;
        _tags = tags;
        _export = export;
        _recordCount = target.RecordCount;
        Target = target;
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    public VersionsTarget Target { get; }

    public string TargetText => _localizer.Format("Export_Target", Target.Name, Labels.For(_localizer, Target.Type));

    /// <summary>
    /// The working copy first, then the versions, newest first.
    /// </summary>
    public ObservableCollection<VersionItemViewModel> Items { get; } = [];

    /// <summary>
    /// What the selected item can be compared with: every other item.
    /// </summary>
    public ObservableCollection<VersionItemViewModel> CompareOptions { get; } = [];

    /// <summary>
    /// Whether the working copy or the versions changed here; the main window reloads afterwards.
    /// </summary>
    public bool HasChangedData { get; private set; }

    /// <summary>
    /// The current loading work; awaited by tests.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkingCopySelected), nameof(IsVersionSelected), nameof(SelectedVersionText))]
    [NotifyCanExecuteChangedFor(nameof(CreateVersionCommand), nameof(ExportVersionCommand), nameof(RollbackCommand), nameof(DeleteVersionCommand))]
    public partial VersionItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial VersionItemViewModel? CompareWith { get; set; }

    public bool IsWorkingCopySelected => SelectedItem?.IsWorkingCopy == true;

    public bool IsVersionSelected => SelectedItem?.IsVersion == true;

    /// <summary>
    /// "Created 12.10.2026 14:03 · 1,234 records (980 approved)" for the selected version.
    /// </summary>
    public string SelectedVersionText => SelectedItem?.Version is { } version
        ? _localizer.Format("Versions_Details", VersionText.Date(version.CreatedAt), version.RecordCount, version.ApprovedCount, version.Tokenizer, version.ContextLength)
        : _localizer["Versions_WorkingCopyDetails"];

    /// <summary>
    /// No versions yet: there is nothing to compare the working copy with.
    /// </summary>
    public bool HasNoVersions => Items.Count <= 1;

    // ===== Comparison =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OlderTitle), nameof(NewerTitle), nameof(ComparisonTitle))]
    public partial VersionItemViewModel? Older { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OlderTitle), nameof(NewerTitle), nameof(ComparisonTitle))]
    public partial VersionItemViewModel? Newer { get; private set; }

    public string OlderTitle => Older?.Title ?? string.Empty;

    public string NewerTitle => Newer?.Title ?? string.Empty;

    /// <summary>
    /// "v1.0.0 → Working copy".
    /// </summary>
    public string ComparisonTitle => Older is null || Newer is null ? string.Empty : $"{Older.Title} → {Newer.Title}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison), nameof(SummaryText), nameof(IsIdentical), nameof(HasDifferences))]
    public partial VersionComparison? Comparison { get; private set; }

    public bool HasComparison => Comparison is not null;

    public bool HasDifferences => Comparison is { Differences.Count: > 0 };

    public bool IsIdentical => Comparison is { Differences.Count: 0 };

    /// <summary>
    /// "3 added · 1 removed · 5 changed · 2 status changes · 1,234 unchanged".
    /// </summary>
    public string SummaryText => Comparison is { } comparison
        ? string.Join(" · ", Enum.GetValues<DifferenceKind>()
            .Select(kind => (kind, count: comparison.Count(kind)))
            .Where(x => x.count > 0)
            .Select(x => _localizer.Format($"Versions_Count{x.kind}", x.count))
            .Append(_localizer.Format("Versions_CountUnchanged", comparison.Unchanged)))
        : string.Empty;

    public ObservableCollection<KindFilterOption> KindFilters { get; } = [];

    [ObservableProperty]
    public partial KindFilterOption? SelectedKindFilter { get; set; }

    /// <summary>
    /// The differences of the chosen kind, in list order.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChangeItemViewModel> Changes { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedChange), nameof(SelectedChangeTitle))]
    public partial ChangeItemViewModel? SelectedChange { get; set; }

    public bool HasSelectedChange => SelectedChange is not null;

    /// <summary>
    /// "#12 · Changed · Draft → Approved".
    /// </summary>
    public string SelectedChangeTitle => SelectedChange is { } change
        ? string.Join(" · ", new[] { change.OrdinalText, change.KindText, change.StatusText }.OfType<string>())
        : string.Empty;

    public ObservableCollection<FieldDiffViewModel> Fields { get; } = [];

    // ===== State =====

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateVersionCommand), nameof(ExportVersionCommand), nameof(RollbackCommand), nameof(DeleteVersionCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasError => ErrorMessage is not null;

    public bool HasNotice => Notice is not null;

    /// <summary>
    /// Asks for a number and a note, then freezes the dataset; <c>null</c> if cancelled.
    /// </summary>
    /// <param name="analysisProgress">Receives how many records were analysed first.</param>
    public static async Task<VersionInfo?> PromptAndCreateAsync(
        IVersionService versions,
        IDialogService dialogs,
        Localizer localizer,
        VersionsTarget target,
        IReadOnlyList<VersionInfo> existing,
        IProgress<int>? analysisProgress)
    {
        var prompt = new NewVersionViewModel(localizer, target.Name, existing.Select(v => v.Number), target.RecordCount);
        if (!await dialogs.PromptNewVersionAsync(prompt))
        {
            return null;
        }

        return await versions.CreateAsync(target.DatasetId, prompt.Number, prompt.Description, analysisProgress);
    }

    /// <summary>
    /// Loads the versions and shows the working copy compared with the latest version.
    /// </summary>
    /// <param name="versionId">A version to show instead.</param>
    public Task InitializeAsync(Guid? versionId = null) => ReloadAsync(versionId);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateVersionAsync()
    {
        await RunAsync(async () =>
        {
            BusyText = _localizer["Versions_Creating"];
            var progress = new Progress<int>(done => BusyText = _localizer.Format("Versions_Analyzing", done));
            var existing = Items.Where(i => i.Version is not null).Select(i => i.Version!).ToList();
            if (await PromptAndCreateAsync(_versions, _dialogs, _localizer, Target with { RecordCount = _recordCount }, existing, progress) is not { } created)
            {
                return;
            }

            HasChangedData = true;
            Notice = _localizer.Format("Versions_Created", created.Number, created.RecordCount);
            await ReloadAsync(created.Id);
        });
    }

    [RelayCommand(CanExecute = nameof(CanActOnVersion))]
    private async Task ExportVersionAsync()
    {
        if (SelectedItem?.Version is not { } version || _export is null)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            await _export(version);
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }
    }

    /// <summary>
    /// Makes the working copy match the selected version, after confirmation; undoable (SDD §14.2).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnVersion))]
    private async Task RollbackAsync()
    {
        if (SelectedItem?.Version is not { } version)
        {
            return;
        }

        await RunAsync(async () =>
        {
            BusyText = _localizer["Versions_Comparing"];
            var changes = await _versions.CompareAsync(Target.DatasetId, version.Id, null);
            var content = changes.Count(DifferenceKind.Modified);
            var status = changes.Count(DifferenceKind.StatusChanged) + changes.Count(DifferenceKind.TagsChanged);
            var deleted = changes.Count(DifferenceKind.Removed);
            var archived = changes.Differences.Count(d => d.Kind == DifferenceKind.Added && d.NewStatus != RecordStatus.Archived);
            if (content + status + deleted + archived == 0)
            {
                Notice = _localizer.Format("Versions_AlreadyAt", version.Number);
                return;
            }

            if (!await _dialogs.ConfirmAsync(
                    _localizer["Dialog_RollbackTitle"],
                    _localizer.Format("Dialog_RollbackMessage", version.Number, content, status, deleted, archived),
                    _localizer["Dialog_RollbackConfirm"]))
            {
                return;
            }

            BusyText = _localizer.Format("Versions_RollingBack", version.Number);
            var result = await _operations.RollbackAsync(Target.DatasetId, version.Id);
            _recordCount += result.Recreated.Count;
            HasChangedData = true;
            Notice = _localizer.Format("Versions_RolledBack", version.Number, result.Changed);
            await CompareAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanActOnVersion))]
    private async Task DeleteVersionAsync()
    {
        if (SelectedItem?.Version is not { } version
            || !await _dialogs.ConfirmAsync(
                _localizer["Dialog_DeleteVersionTitle"],
                _localizer.Format("Dialog_DeleteVersionMessage", version.Number),
                _localizer["Action_Delete"],
                isDestructive: true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _versions.DeleteAsync(version.Id);
            HasChangedData = true;
            Notice = _localizer.Format("Versions_Deleted", version.Number);
            await ReloadAsync(null);
        });
    }

    [RelayCommand]
    private void Close()
    {
        _work?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanCreate() => !IsBusy && IsWorkingCopySelected;

    private bool CanActOnVersion() => !IsBusy && IsVersionSelected;

    partial void OnSelectedItemChanged(VersionItemViewModel? value) => ApplySelection(value);

    // The list selects items by itself while it fills (a list that always keeps a selection), so
    // the comparison is set up here and again once the list is complete (see ReloadAsync).
    private void ApplySelection(VersionItemViewModel? value)
    {
        _selecting = true;
        try
        {
            CompareOptions.Clear();
            foreach (var item in Items.Where(i => i != value))
            {
                CompareOptions.Add(item);
            }

            // By default the one before it; the first version is compared with the working copy.
            var index = value is null ? -1 : Items.IndexOf(value);
            CompareWith = index < 0 ? null : index + 1 < Items.Count ? Items[index + 1] : index > 0 ? Items[0] : null;
        }
        finally
        {
            _selecting = false;
        }

        _ = CompareAsync();
    }

    partial void OnCompareWithChanged(VersionItemViewModel? value)
    {
        if (!_selecting)
        {
            _ = CompareAsync();
        }
    }

    partial void OnSelectedKindFilterChanged(KindFilterOption? value) => ApplyKindFilter(value);

    private void ApplyKindFilter(KindFilterOption? value)
    {
        Changes = value?.Kind is { } kind ? [.. _allChanges.Where(c => c.Kind == kind)] : _allChanges;
        SelectedChange = Changes.FirstOrDefault();
    }

    partial void OnSelectedChangeChanged(ChangeItemViewModel? value) => _ = ShowChangeAsync(value);

    private async Task ReloadAsync(Guid? selectVersionId)
    {
        ErrorMessage = null;
        try
        {
            var versions = await _versions.ListAsync(Target.DatasetId);
            Items.Clear();
            Items.Add(VersionItemViewModel.WorkingCopy(_localizer));
            foreach (var version in versions)
            {
                Items.Add(VersionItemViewModel.For(_localizer, version));
            }

            OnPropertyChanged(nameof(HasNoVersions));
            var selected = Items.FirstOrDefault(i => i.VersionId == selectVersionId && selectVersionId is not null) ?? Items[0];
            if (SelectedItem == selected)
            {
                ApplySelection(selected); // selected by the list while it filled, before the others were there
            }
            else
            {
                SelectedItem = selected;
            }

            await PendingWork;
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }
    }

    /// <summary>
    /// Compares the selected item with the chosen one, older to newer; a newer call replaces an older one.
    /// </summary>
    private Task CompareAsync()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        PendingWork = RunCore();
        return PendingWork;

        async Task RunCore()
        {
            SelectedChange = null;
            Changes = [];
            _allChanges = [];
            KindFilters.Clear();
            Comparison = null;
            if (SelectedItem is not { } selected || CompareWith is not { } other)
            {
                Older = Newer = null;
                return;
            }

            // Items are newest first: the one further down is older.
            (Older, Newer) = Items.IndexOf(selected) > Items.IndexOf(other) ? (selected, other) : (other, selected);
            try
            {
                var comparison = await _versions.CompareAsync(Target.DatasetId, Older.VersionId, Newer.VersionId, token);
                token.ThrowIfCancellationRequested();
                _allChanges = [.. comparison.Differences.Select(ToItem)];
                KindFilters.Add(new KindFilterOption(null, _localizer.Format("Versions_FilterAll", comparison.Differences.Count)));
                foreach (var kind in Enum.GetValues<DifferenceKind>())
                {
                    if (comparison.Count(kind) is var count and > 0)
                    {
                        KindFilters.Add(new KindFilterOption(kind, _localizer.Format("Versions_Filter", VersionText.Kind(_localizer, kind), count)));
                    }
                }

                Comparison = comparison;
                if (SelectedKindFilter == KindFilters[0])
                {
                    ApplyKindFilter(KindFilters[0]); // selected by the chips while they filled
                }
                else
                {
                    SelectedKindFilter = KindFilters[0];
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Replaced by a newer comparison.
            }
            catch (Exception ex) when (Describe(ex) is { } message)
            {
                ErrorMessage = message;
            }
        }
    }

    private async Task ShowChangeAsync(ChangeItemViewModel? change)
    {
        Fields.Clear();
        if (change is null || Older is not { } older || Newer is not { } newer)
        {
            return;
        }

        try
        {
            var difference = change.Difference;
            var oldPayload = difference.Kind == DifferenceKind.Added
                ? null
                : await _versions.GetContentAsync(Target.DatasetId, older.VersionId, difference.RecordId);
            var newPayload = difference.Kind == DifferenceKind.Removed
                ? null
                : await _versions.GetContentAsync(Target.DatasetId, newer.VersionId, difference.RecordId);
            if (SelectedChange != change)
            {
                return; // another record was selected meanwhile
            }

            foreach (var field in BuildFields(oldPayload, newPayload))
            {
                Fields.Add(field);
            }

            if (TagsField(difference) is { } tags)
            {
                Fields.Add(tags);
            }
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }
    }

    private List<FieldDiffViewModel> BuildFields(RecordPayload? oldPayload, RecordPayload? newPayload)
    {
        var fields = new List<FieldDiffViewModel>();
        var wholeRecord = oldPayload is null || newPayload is null;
        foreach (var field in FieldDiff.Compare(oldPayload, newPayload))
        {
            var label = VersionText.Field(_localizer, (field.New ?? field.Old)!);
            if (wholeRecord)
            {
                // A record added or removed as a whole: its texts as they are.
                var text = (field.New ?? field.Old)!.Text;
                IReadOnlyList<DiffSegment> plain = VersionText.OrEmpty(_localizer, text.Length == 0 ? [] : [new DiffSegment(DiffKind.Equal, text)]);
                fields.Add(new FieldDiffViewModel(label, null, field.Old is null ? [] : plain, field.New is null ? [] : plain, field.Old is not null, field.New is not null));
                continue;
            }

            var badge = !field.IsChanged ? null
                : field.Old is null ? _localizer["Versions_FieldAdded"]
                : field.New is null ? _localizer["Versions_FieldRemoved"]
                : _localizer["Versions_FieldChanged"];
            fields.Add(new FieldDiffViewModel(
                label,
                badge,
                field.Old is null ? [] : VersionText.OrEmpty(_localizer, field.Diff.OldSide),
                field.New is null ? [] : VersionText.OrEmpty(_localizer, field.Diff.NewSide),
                field.Old is not null,
                field.New is not null));
        }

        return fields;
    }

    // The tags as one more field: removed ones struck on the older side, added ones on the newer.
    private FieldDiffViewModel? TagsField(RecordDifference difference)
    {
        var older = difference.OldTagIds is { } o ? _tags.Resolve(o) : null;
        var newer = difference.NewTagIds is { } n ? _tags.Resolve(n) : null;
        if ((older?.Count ?? 0) == 0 && (newer?.Count ?? 0) == 0)
        {
            return null;
        }

        var label = _localizer["Field_Tags"];
        if (older is null || newer is null || !difference.TagsDiffer)
        {
            var plain = Segments(older ?? newer!, _ => false, DiffKind.Equal);
            return new FieldDiffViewModel(label, null, older is null ? [] : plain, newer is null ? [] : plain, older is not null, newer is not null);
        }

        var olderIds = older.Select(t => t.Id).ToHashSet();
        var newerIds = newer.Select(t => t.Id).ToHashSet();
        return new FieldDiffViewModel(
            label,
            _localizer["Versions_FieldChanged"],
            Segments(older, t => !newerIds.Contains(t.Id), DiffKind.Deleted),
            Segments(newer, t => !olderIds.Contains(t.Id), DiffKind.Inserted),
            HasOld: true,
            HasNew: true);

        IReadOnlyList<DiffSegment> Segments(IReadOnlyList<TagViewModel> tags, Func<TagViewModel, bool> marked, DiffKind kind) =>
            VersionText.OrEmpty(_localizer, [.. tags.SelectMany((tag, i) => new[]
            {
                new DiffSegment(DiffKind.Equal, i == 0 ? string.Empty : ", "),
                new DiffSegment(marked(tag) ? kind : DiffKind.Equal, tag.Name),
            }).Where(segment => segment.Text.Length > 0)]);
    }

    private ChangeItemViewModel ToItem(RecordDifference difference)
    {
        var status = (difference.OldStatus, difference.NewStatus) switch
        {
            ({ } from, { } to) when from != to => $"{Labels.For(_localizer, from)} → {Labels.For(_localizer, to)}",
            (_, { } to) when difference.Kind == DifferenceKind.Added => Labels.For(_localizer, to),
            _ => null,
        };
        var preview = difference.Preview.Length == 0 ? _localizer["Field_Empty"] : difference.Preview;
        return new ChangeItemViewModel(difference, VersionText.Kind(_localizer, difference.Kind), preview, status);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        Notice = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private string? Describe(Exception exception) =>
        UiErrors.TryDescribe(exception) is { } error ? _localizer.Format(error.Key, error.Args) : null;
}
