using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Analysis;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Judgments;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Application.Versions;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Desktop.ViewModels.Tags;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// The inspector: edits the selected record with fields that match its type (SDD §6.1), offers
/// the review actions its status allows and its tags, and turns into a batch panel when several
/// records are selected. Edits are saved explicitly or automatically when another record is shown.
/// </summary>
public sealed partial class RecordDetailsViewModel : ViewModelBase
{
    private readonly IRecordQueryService _queries;
    private readonly RecordOperations _operations;
    private readonly IDialogService _dialogs;
    private readonly StatusBarViewModel _status;
    private readonly Localizer _localizer;
    private readonly IRecordAnalysisService _analysis;
    private readonly ITokenizerProvider _tokenizers;
    private readonly TagCatalog _tags;
    private readonly AIAssistant _ai;
    private readonly RecordJudge _judge;
    private PiiScanner _scanner = new();
    private IReadOnlyList<QualityIssue> _crossIssues = [];
    private PiiMaskResult? _mask;
    private int _generation;
    private string _cleanHash = string.Empty;
    private bool _loadingEditor;
    private Guid _batchDatasetId;

    public RecordDetailsViewModel(
        IRecordQueryService queries,
        RecordOperations operations,
        IRecordAnalysisService analysis,
        ITokenizerProvider tokenizers,
        IDialogService dialogs,
        StatusBarViewModel status,
        Localizer localizer,
        TagCatalog tags,
        AIAssistant ai,
        RecordJudge judge)
    {
        _queries = queries;
        _analysis = analysis;
        _tokenizers = tokenizers;
        _operations = operations;
        _dialogs = dialogs;
        _status = status;
        _localizer = localizer;
        _tags = tags;
        _ai = ai;
        _judge = judge;
        _ai.ConnectionChanged += (_, _) => OnPropertyChanged(nameof(WritingText));
        TagPicker = new TagPickerViewModel(tags, localizer, status, tag => SetTagAsync(tag, present: true));
        BatchTagPicker = new TagPickerViewModel(tags, localizer, status, tag => SetTagAsync(tag, present: true));
        _tags.Changed += (_, _) => RefreshTags();
        _localizer.CultureChanged += (_, _) => Refresh();
    }

    // ===== Single record =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecord), nameof(IsEmpty), nameof(Title), nameof(Status), nameof(StatusLabel), nameof(Info), nameof(IsJudgmentOutdated))]
    [NotifyPropertyChangedFor(nameof(IsChat), nameof(ShowChatEditor), nameof(IsArchived), nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(IsPreference), nameof(ShowPreferenceCompare))]
    [NotifyPropertyChangedFor(nameof(ShowApprove), nameof(ShowSendToReview), nameof(ShowArchive), nameof(ShowRestore))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SendToReviewCommand), nameof(ArchiveCommand), nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(SaveCommand), nameof(AddMessageCommand), nameof(TogglePiiPreviewCommand), nameof(ApplyMaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenTagPickerCommand), nameof(TogglePreferenceCompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(WriteMainCommand), nameof(JudgeCommand))]
    public partial RecordDetails? Record { get; private set; }

    /// <summary>
    /// The editor content differs from the saved record.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial bool IsDirty { get; private set; }

    public bool HasRecord => Record is not null;

    /// <summary>
    /// Nothing is selected.
    /// </summary>
    public bool IsEmpty => !HasRecord && !IsBatch;

    public string Title => Record is { } record ? _localizer.Format("Details_Title", record.Ordinal) : string.Empty;

    public RecordStatus Status => Record?.Status ?? RecordStatus.Draft;

    public string StatusLabel => Record is { } record ? Labels.For(_localizer, record.Status) : string.Empty;

    public IReadOnlyList<InfoItem> Info => Record is { } record ? BuildInfo(record) : [];

    /// <summary>
    /// The passage of a document the record was written from, so its answer can be checked against it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    public partial PassageSource? Source { get; private set; }

    public bool HasSource => Source is not null;

    /// <summary>
    /// The passage is shown under the record's information; it stays open from record to record.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSourceShown { get; set; }

    /// <summary>
    /// Text fields of the record; for chats only the system prompt (turns are in <see cref="Messages"/>).
    /// </summary>
    public ObservableCollection<EditorField> Fields { get; } = [];

    public ObservableCollection<EditorMessage> Messages { get; } = [];

    public bool IsChat => Record?.Payload is ChatPayload;

    /// <summary>
    /// Archived records are read-only until restored (SDD §5.2).
    /// </summary>
    public bool IsArchived => Record?.Status == RecordStatus.Archived;

    public bool IsEditable => HasRecord && !IsArchived;

    // Only the actions that make sense for the current status are shown.
    public bool ShowApprove => CanApprove();

    public bool ShowSendToReview => CanSendToReview();

    public bool ShowArchive => CanArchive();

    public bool ShowRestore => CanRestore();

    // ===== Tokens and quality (SDD §7.3, §8) =====

    /// <summary>
    /// The dataset of the shown records, for its tokenizer and context length.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVary), nameof(CanJudgeRecords))]
    [NotifyCanExecuteChangedFor(nameof(JudgeCommand))]
    public partial DatasetNodeViewModel? Dataset { get; set; }

    /// <summary>
    /// Live token counts of the editor content; <c>null</c> without a record.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens), nameof(TokenText), nameof(TokenDetail), nameof(TokenTone), nameof(IsTokenGood), nameof(IsTokenFair), nameof(IsTokenPoor))]
    public partial TokenCounts? Tokens { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQualityIssues), nameof(HasNoQualityIssues))]
    public partial IReadOnlyList<IssueItem> QualityIssues { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityScoreText), nameof(QualityTone), nameof(IsQualityGood), nameof(IsQualityFair), nameof(IsQualityPoor))]
    public partial int? QualityScore { get; private set; }

    public bool HasTokens => Tokens is not null;

    /// <summary>
    /// "812 / 4,096" — content tokens against the context length.
    /// </summary>
    public string TokenText => Tokens is { } t && Dataset is { } d
        ? _localizer.Format("Editor_Tokens", t.Total, d.ContextLength)
        : string.Empty;

    public string TokenDetail => Tokens is { } t && Dataset is { } d
        ? _localizer.Format("Editor_TokenDetail", t.Input, t.Output, d.Tokenizer)
        : string.Empty;

    /// <summary>
    /// Green below half the context length, amber up to it, red beyond (SDD §6.1).
    /// </summary>
    public ScoreTone TokenTone => Tokens is { } t && Dataset is { } d
        ? t.Total > d.ContextLength ? ScoreTone.Poor : t.Total * 2 >= d.ContextLength ? ScoreTone.Fair : ScoreTone.Good
        : ScoreTone.None;

    public bool IsTokenGood => TokenTone == ScoreTone.Good;

    public bool IsTokenFair => TokenTone == ScoreTone.Fair;

    public bool IsTokenPoor => TokenTone == ScoreTone.Poor;

    public bool HasQualityIssues => QualityIssues.Count > 0;

    public bool HasNoQualityIssues => HasRecord && QualityIssues.Count == 0;

    public string QualityScoreText => QualityScore?.ToString(CultureInfo.CurrentCulture) ?? "—";

    public ScoreTone QualityTone => RecordRowViewModel.ToneOf(QualityScore);

    public bool IsQualityGood => QualityTone == ScoreTone.Good;

    public bool IsQualityFair => QualityTone == ScoreTone.Fair;

    public bool IsQualityPoor => QualityTone == ScoreTone.Poor;

    /// <summary>
    /// Words the workspace treats as personal data (company names, code names…).
    /// </summary>
    public IReadOnlyList<string> CustomTerms
    {
        get => _scanner.CustomTerms;
        set
        {
            _scanner = new PiiScanner(value);
            Reanalyze();
        }
    }

    // ===== Personal data preview (SDD §9) =====

    /// <summary>
    /// Shows the record with personal data masked instead of the editor (Mod+Shift+M).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(ShowChatEditor), nameof(ShowPreferenceCompare))]
    [NotifyCanExecuteChangedFor(nameof(ApplyMaskCommand))]
    public partial bool IsPiiPreview { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<PreviewBlock> PiiPreview { get; private set; } = [];

    [ObservableProperty]
    public partial string PiiSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyMaskCommand))]
    public partial bool HasPii { get; private set; }

    /// <summary>
    /// The editable fields are shown (not the masked preview).
    /// </summary>
    public bool IsEditing => !IsPiiPreview;

    /// <summary>
    /// The chat turns and their "add message" button are shown.
    /// </summary>
    public bool ShowChatEditor => IsChat && IsEditing;

    // ===== Preference pairs: chosen against rejected (SDD §6.1) =====

    public bool IsPreference => Record?.Payload is PreferencePayload;

    /// <summary>
    /// Shows below the answers what differs between them. Stays on from record to record.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreferenceCompare))]
    public partial bool IsPreferenceCompare { get; private set; }

    public bool ShowPreferenceCompare => IsPreference && IsPreferenceCompare && IsEditing;

    /// <summary>
    /// The rejected answer with what only it says marked (red).
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<DiffSegment> RejectedSegments { get; private set; } = [];

    /// <summary>
    /// The chosen answer with what only it says marked (green).
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<DiffSegment> ChosenSegments { get; private set; } = [];

    /// <summary>
    /// "12 words only in the chosen answer · 3 only in the rejected one".
    /// </summary>
    [ObservableProperty]
    public partial string PreferenceCompareSummary { get; private set; } = string.Empty;

    // ===== Several records =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatch), nameof(IsEmpty), nameof(BatchTitle), nameof(BatchCounts))]
    [NotifyPropertyChangedFor(nameof(ShowBatchApprove), nameof(ShowBatchSendToReview), nameof(ShowBatchArchive), nameof(ShowBatchRestore))]
    [NotifyCanExecuteChangedFor(nameof(OpenBatchTagPickerCommand))]
    public partial IReadOnlyList<RecordRowViewModel> BatchRows { get; private set; } = [];

    public bool IsBatch => BatchRows.Count > 1;

    public string BatchTitle => _localizer.Format("Batch_Title", BatchRows.Count);

    public IReadOnlyList<StatusCount> BatchCounts =>
        [.. BatchRows.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => new StatusCount(g.Key, Labels.For(_localizer, g.Key), g.Count()))];

    public bool ShowBatchApprove => BatchCan(RecordStatus.Approved);

    public bool ShowBatchSendToReview => BatchCan(RecordStatus.InReview);

    public bool ShowBatchArchive => BatchCan(RecordStatus.Archived);

    public bool ShowBatchRestore => BatchRows.Any(r => r.Status == RecordStatus.Archived);

    // ===== Tags (SDD §6.2) =====

    /// <summary>
    /// The tags of the shown record, by name.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<TagViewModel> RecordTags { get; private set; } = [];

    /// <summary>
    /// "Add tag" for the shown record.
    /// </summary>
    public TagPickerViewModel TagPicker { get; }

    /// <summary>
    /// "Add tag" for all selected records.
    /// </summary>
    public TagPickerViewModel BatchTagPicker { get; }

    /// <summary>
    /// The tags of the selected records, with how many of them carry each.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBatchTags))]
    public partial IReadOnlyList<TagCountItem> BatchTags { get; private set; } = [];

    public bool HasBatchTags => BatchTags.Count > 0;

    /// <summary>
    /// Shows one record (or nothing), saving unsaved edits of the previous one first.
    /// </summary>
    public async Task ShowAsync(Guid? recordId)
    {
        var generation = ++_generation;
        await StopWritingAsync();
        await StopJudgingAsync();
        await SaveIfDirtyAsync();
        BatchRows = [];
        if (recordId is null)
        {
            SetRecord(null);
            return;
        }

        if (Record?.Id == recordId)
        {
            return;
        }

        try
        {
            var record = await _queries.GetAsync(recordId.Value);
            if (generation == _generation)
            {
                SetRecord(record);
                await LoadCrossIssuesAsync(generation);
                await LoadJudgmentAsync(generation);
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    /// <summary>
    /// Shows the batch panel for several selected records.
    /// </summary>
    public async Task ShowBatchAsync(Guid datasetId, IReadOnlyList<RecordRowViewModel> rows)
    {
        ++_generation;
        await StopWritingAsync();
        await StopJudgingAsync();
        await SaveIfDirtyAsync();
        SetRecord(null);
        _batchDatasetId = datasetId;
        BatchRows = rows;
    }

    /// <summary>
    /// Reloads the shown record, e.g. after undo changed it.
    /// </summary>
    public async Task ReloadAsync()
    {
        if (Record is not { } record)
        {
            return;
        }

        var generation = ++_generation;
        await StopWritingAsync();
        try
        {
            var fresh = await _queries.GetAsync(record.Id);
            if (generation == _generation)
            {
                SetRecord(fresh);
                await LoadJudgmentAsync(generation);
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    /// <summary>
    /// Saves pending edits, if any.
    /// </summary>
    /// <returns><c>false</c> if saving failed.</returns>
    public async Task<bool> SaveIfDirtyAsync()
    {
        // A model still writing is stopped: what it wrote so far is saved.
        await StopWritingAsync();
        if (!IsDirty || Record is not { } before || IsArchived)
        {
            return true;
        }

        var payload = BuildPayload(before.Payload);
        var writtenBy = _writtenBy;
        // Clean from now on, so a second call while this save runs does not save again.
        _cleanHash = payload.ComputeContentHash();
        IsDirty = false;
        _writtenBy = null;
        try
        {
            if (await _operations.SaveAsync(before, payload, writtenBy))
            {
                _status.Set("Status_RecordSaved", before.Ordinal);
                if (Record?.Id == before.Id && await _queries.GetAsync(before.Id) is { } fresh && Record?.Id == before.Id)
                {
                    Record = fresh; // header, status and info; the fields already show this content
                    await LoadCrossIssuesAsync(_generation);
                }
            }

            return true;
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
            _cleanHash = before.ContentHash;
            _writtenBy = writtenBy;
            OnEdited();
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveIfDirtyAsync();

    /// <summary>
    /// Throws away unsaved edits.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Discard()
    {
        if (Record is { } record)
        {
            LoadEditor(record);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddMessage))]
    private void AddMessage()
    {
        // Alternate turns: an assistant answer follows a user message and vice versa.
        var role = Messages.LastOrDefault()?.Role == ChatRole.User ? ChatRole.Assistant : ChatRole.User;
        Messages.Add(CreateMessage(role, string.Empty));
        OnEdited();
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private Task ApproveAsync() => ChangeStatusAsync(RecordStatus.Approved);

    [RelayCommand(CanExecute = nameof(CanSendToReview))]
    private Task SendToReviewAsync() => ChangeStatusAsync(RecordStatus.InReview);

    [RelayCommand(CanExecute = nameof(CanArchive))]
    private Task ArchiveAsync() => ChangeStatusAsync(RecordStatus.Archived);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreAsync() => ChangeStatusAsync(RecordStatus.Draft);

    [RelayCommand(CanExecute = nameof(HasRecord))]
    private async Task DeleteAsync()
    {
        if (Record is not { } record
            || !await _dialogs.ConfirmAsync(
                _localizer["Dialog_DeleteRecordTitle"],
                _localizer.Format("Dialog_DeleteRecordMessage", record.Ordinal),
                _localizer["Action_Delete"],
                isDestructive: true))
        {
            return;
        }

        try
        {
            IsDirty = false; // the edits go with the record
            if (await _operations.DeleteAsync(record.DatasetId, [record.Id]) > 0)
            {
                SetRecord(null);
                _status.Set("Status_RecordDeleted", record.Ordinal);
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    [RelayCommand]
    private Task BatchApproveAsync() => ChangeBatchStatusAsync(RecordStatus.Approved);

    [RelayCommand]
    private Task BatchSendToReviewAsync() => ChangeBatchStatusAsync(RecordStatus.InReview);

    [RelayCommand]
    private Task BatchArchiveAsync() => ChangeBatchStatusAsync(RecordStatus.Archived);

    [RelayCommand]
    private Task BatchRestoreAsync() => ChangeBatchStatusAsync(RecordStatus.Draft);

    [RelayCommand]
    private async Task BatchDeleteAsync()
    {
        var rows = BatchRows;
        if (rows.Count == 0
            || !await _dialogs.ConfirmAsync(
                _localizer["Dialog_DeleteRecordsTitle"],
                _localizer.Format("Dialog_DeleteRecordsMessage", rows.Count),
                _localizer["Action_Delete"],
                isDestructive: true))
        {
            return;
        }

        try
        {
            var deleted = await _operations.DeleteAsync(_batchDatasetId, rows.Select(r => r.Id).ToList());
            BatchRows = [];
            _status.Set("Status_RecordsDeleted", deleted);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    [RelayCommand(CanExecute = nameof(HasRecord))]
    private void OpenTagPicker() => TagPicker.Open(() => Record?.TagIds ?? []);

    [RelayCommand(CanExecute = nameof(IsBatch))]
    private void OpenBatchTagPicker() => BatchTagPicker.Open(TagsOnEveryRow);

    /// <summary>
    /// Takes a tag off the shown record, or off all selected records.
    /// </summary>
    [RelayCommand]
    private Task RemoveTagAsync(TagViewModel? tag) => tag is null ? Task.CompletedTask : SetTagAsync(tag, present: false);

    /// <summary>
    /// Mod+1…5: adds the tag, or removes it when the record (every selected record) already has it.
    /// </summary>
    public Task ToggleTagAsync(TagViewModel tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (IsBatch)
        {
            return SetTagAsync(tag, present: !TagsOnEveryRow().Contains(tag.Id));
        }

        return Record is { } record ? SetTagAsync(tag, present: !record.TagIds.Contains(tag.Id)) : Task.CompletedTask;
    }

    private async Task SetTagAsync(TagViewModel tag, bool present)
    {
        try
        {
            if (IsBatch)
            {
                var changed = await _operations.SetTagAsync(_batchDatasetId, BatchRows.Select(r => r.Id).ToList(), tag.Id, tag.Name, present);
                _status.Set(present ? "Status_TagAddedMany" : "Status_TagRemovedMany", changed.Count, tag.Name);
                RefreshTags(); // the rows were updated by the list
            }
            else if (Record is { } record)
            {
                var changed = await _operations.SetTagAsync(record.DatasetId, [record.Id], tag.Id, tag.Name, present, record.Ordinal);
                if (changed.Count > 0 && Record is { } shown && shown.Id == record.Id)
                {
                    Record = shown with { TagIds = present ? [.. shown.TagIds, tag.Id] : [.. shown.TagIds.Where(id => id != tag.Id)] };
                }

                _status.Set(present ? "Status_TagAdded" : "Status_TagRemoved", record.Ordinal, tag.Name);
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    // The tags all selected records have: "add" leaves them out, toggling removes them.
    private HashSet<Guid> TagsOnEveryRow()
    {
        var rows = BatchRows;
        var common = rows.Count == 0 ? [] : rows[0].TagIds.ToHashSet();
        foreach (var row in rows.Skip(1))
        {
            common.IntersectWith(row.TagIds);
        }

        return common;
    }

    partial void OnRecordChanged(RecordDetails? value)
    {
        RecordTags = value is null ? [] : _tags.Resolve(value.TagIds);
        Source = value is null ? null : PassageSource.FromMetadata(value.MetadataJson);
    }

    // Back from the masked preview the comparison is shown again.
    partial void OnIsPiiPreviewChanged(bool value)
    {
        UpdatePreferenceCompare();
        WriteCommand.NotifyCanExecuteChanged();
        WriteMainCommand.NotifyCanExecuteChanged();
    }

    partial void OnBatchRowsChanged(IReadOnlyList<RecordRowViewModel> value) => RefreshBatchTags();

    // Tags were added, renamed, recolored or deleted.
    private void RefreshTags()
    {
        RecordTags = Record is { } record ? _tags.Resolve(record.TagIds) : [];
        RefreshBatchTags();
    }

    private void RefreshBatchTags() =>
        BatchTags = [.. _tags.Resolve(BatchRows.SelectMany(r => r.TagIds).Distinct())
            .Select(tag => new TagCountItem(tag, BatchRows.Count(r => r.TagIds.Contains(tag.Id))))];

    private bool CanSave() => IsDirty && IsEditable && !IsWriting;

    private bool CanAddMessage() => IsChat && IsEditable;

    // While edits are pending, approving saves them first (the record goes to review, then is approved).
    private bool CanApprove() => Can(RecordStatus.Approved);

    private bool CanSendToReview() => Can(RecordStatus.InReview);

    private bool CanArchive() => Can(RecordStatus.Archived);

    // Restoring is the only way out of Archived; Archived → Draft is also allowed elsewhere, but
    // the button is only meaningful for archived records.
    private bool CanRestore() => Record?.Status == RecordStatus.Archived;

    private bool Can(RecordStatus target) =>
        Record is { } record && record.Status != target && RecordStatusTransitions.CanTransition(record.Status, target);

    private bool BatchCan(RecordStatus target) =>
        BatchRows.Any(r => r.Status != target && RecordStatusTransitions.CanTransition(r.Status, target));

    private async Task ChangeStatusAsync(RecordStatus status)
    {
        if (!await SaveIfDirtyAsync() || Record is not { } record || !Can(status))
        {
            return;
        }

        try
        {
            var result = await _operations.SetStatusAsync(record.DatasetId, [record.Id], status, record.Ordinal);
            if (result.Changed == 1 && Record?.Id == record.Id)
            {
                Record = record with { Status = status };
                _status.Set("Status_RecordStatusChanged", record.Ordinal, Labels.For(_localizer, status));
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    private async Task ChangeBatchStatusAsync(RecordStatus status)
    {
        var rows = BatchRows;
        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            var result = await _operations.SetStatusAsync(_batchDatasetId, rows.Select(r => r.Id).ToList(), status);
            _status.Set(
                result.Skipped == 0 ? "Status_RecordsStatusChangedAll" : "Status_RecordsStatusChanged",
                result.Changed,
                Labels.For(_localizer, status),
                result.Skipped);
            OnPropertyChanged(nameof(BatchCounts)); // the rows were updated by the list
            OnPropertyChanged(nameof(ShowBatchApprove));
            OnPropertyChanged(nameof(ShowBatchSendToReview));
            OnPropertyChanged(nameof(ShowBatchArchive));
            OnPropertyChanged(nameof(ShowBatchRestore));
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    [RelayCommand(CanExecute = nameof(IsPreference))]
    private void TogglePreferenceCompare()
    {
        IsPreferenceCompare = !IsPreferenceCompare;
        UpdatePreferenceCompare();
    }

    // Word-level, as the versions window compares (TextDiff); follows every keystroke while shown.
    private void UpdatePreferenceCompare()
    {
        if (!ShowPreferenceCompare || Fields.Count < 3)
        {
            (RejectedSegments, ChosenSegments, PreferenceCompareSummary) = ([], [], string.Empty);
            return;
        }

        var (chosen, rejected) = (Fields[1].Text, Fields[2].Text);
        var diff = TextDiff.Compute(rejected, chosen);
        RejectedSegments = Versions.VersionText.OrEmpty(_localizer, diff.OldSide);
        ChosenSegments = Versions.VersionText.OrEmpty(_localizer, diff.NewSide);
        PreferenceCompareSummary = diff.HasChanges
            ? _localizer.Format("Editor_PreferenceCompareSummary", Words(DiffKind.Inserted), Words(DiffKind.Deleted))
            : _localizer["Editor_PreferenceCompareSame"];

        int Words(DiffKind kind) => diff.Segments.Where(segment => segment.Kind == kind)
            .Sum(segment => TextDiff.Tokenize(segment.Text).Count(token => token.Any(char.IsLetterOrDigit)));
    }

    [RelayCommand(CanExecute = nameof(HasRecord))]
    private void TogglePiiPreview()
    {
        IsPiiPreview = !IsPiiPreview;
        UpdatePiiPreview();
    }

    /// <summary>
    /// Puts the masked text into the editor; saving makes it permanent (and undoable).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyMask))]
    private void ApplyMask()
    {
        if (_mask is not { HasFindings: true } mask)
        {
            return;
        }

        switch (mask.Payload)
        {
            case InstructionPayload p:
                (Field("Field_Instruction").Text, Field("Field_Input").Text, Field("Field_Output").Text) = (p.Instruction, p.Input, p.Output);
                Field("Field_Reasoning").Text = p.Reasoning ?? string.Empty;
                break;
            case ChatPayload p:
                Fields[0].Text = p.SystemPrompt ?? string.Empty;
                for (var i = 0; i < Messages.Count && i < p.Messages.Count; i++)
                {
                    Messages[i].Text = p.Messages[i].Content;
                    if (p.Messages[i].Reasoning is { } reasoning)
                    {
                        Messages[i].Reasoning = reasoning;
                    }
                }

                break;
            case PreferencePayload p:
                (Fields[0].Text, Fields[1].Text, Fields[2].Text) = (p.Prompt, p.Chosen, p.Rejected);
                break;
        }

        IsPiiPreview = false;
        _status.Set("Status_MaskApplied");
    }

    private bool CanApplyMask() => IsPiiPreview && HasPii && IsEditable;

    partial void OnDatasetChanged(DatasetNodeViewModel? oldValue, DatasetNodeViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnDatasetSettingsChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnDatasetSettingsChanged;
        }

        Reanalyze();
    }

    // Another tokenizer or context length changes the counts and the limit check.
    private void OnDatasetSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DatasetNodeViewModel.Tokenizer) or nameof(DatasetNodeViewModel.ContextLength))
        {
            Reanalyze();
        }
    }

    /// <summary>
    /// Reads again the issues that compare the shown record with others (after a regrouping).
    /// </summary>
    public Task RefreshQualityAsync() => HasRecord ? LoadCrossIssuesAsync(_generation) : Task.CompletedTask;

    private async Task LoadCrossIssuesAsync(int generation)
    {
        if (Record is not { } record)
        {
            return;
        }

        try
        {
            // Copies and conflicting answers compare records, so they come from the stored analysis.
            var quality = await _analysis.GetRecordQualityAsync(record.Id);
            if (generation == _generation && Record?.Id == record.Id)
            {
                _crossIssues = quality?.Issues.Where(i => i.Rule is QualityRule.Duplicate or QualityRule.ConflictingAnswer or QualityRule.NearDuplicate).ToList() ?? [];
                Reanalyze();
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    // Token counts and single-record rules are cheap, so they follow every keystroke.
    private void Reanalyze()
    {
        if (Record is not { } record || Dataset is not { } dataset || !_tokenizers.Available.Contains(dataset.Tokenizer))
        {
            Tokens = null;
            QualityScore = null;
            QualityIssues = [];
            OnPropertyChanged(nameof(HasNoQualityIssues));
            return;
        }

        var result = new RecordAnalyzer(_tokenizers.Get(dataset.Tokenizer), _scanner).Analyze(BuildPayload(record.Payload));
        var issues = result.Issues.Concat(_crossIssues).ToList();
        if (result.Tokens.Total > dataset.ContextLength)
        {
            issues.Add(new QualityIssue(QualityRule.OverContextLimit, IssueSeverity.Error));
        }

        Tokens = result.Tokens;
        QualityScore = QualityRules.Score(issues);
        QualityIssues = issues.OrderByDescending(i => i.Severity).Select(i => new IssueItem(i.Rule, i.Severity, IssueText.Describe(_localizer, i))).ToList();
        OnPropertyChanged(nameof(TokenText));
        OnPropertyChanged(nameof(TokenDetail));
        OnPropertyChanged(nameof(TokenTone));
        OnPropertyChanged(nameof(IsTokenGood));
        OnPropertyChanged(nameof(IsTokenFair));
        OnPropertyChanged(nameof(IsTokenPoor));
        OnPropertyChanged(nameof(HasNoQualityIssues));
        if (IsPiiPreview)
        {
            UpdatePiiPreview();
        }
    }

    private void UpdatePiiPreview()
    {
        if (!IsPiiPreview || Record is not { } record)
        {
            _mask = null;
            PiiPreview = [];
            HasPii = false;
            return;
        }

        var mask = _scanner.Mask(BuildPayload(record.Payload));
        _mask = mask;
        HasPii = mask.HasFindings;
        PiiSummary = mask.HasFindings ? IssueText.PiiCounts(_localizer, mask.Counts) : _localizer["Pii_NoneFound"];
        PiiPreview = mask.Payload switch
        {
            InstructionPayload p =>
            [
                Block("Field_Instruction", p.Instruction),
                Block("Field_Input", p.Input),
                .. Reasoning(p.Reasoning),
                Block("Field_Output", p.Output),
            ],
            ChatPayload p =>
            [
                .. p.SystemPrompt is null ? [] : new[] { Block("Field_SystemPrompt", p.SystemPrompt, FieldTone.System) },
                .. p.Messages.SelectMany(m => Reasoning(m.Reasoning).Append(Block($"Field_{m.Role}", m.Content))),
            ],
            PreferencePayload p =>
            [
                Block("Field_Prompt", p.Prompt),
                Block("Field_Chosen", p.Chosen, FieldTone.Positive),
                Block("Field_Rejected", p.Rejected, FieldTone.Negative),
            ],
            _ => [],
        };

        PreviewBlock Block(string labelKey, string text, FieldTone tone = FieldTone.Neutral) =>
            new(_localizer[labelKey], string.IsNullOrEmpty(text) ? _localizer["Field_Empty"] : text, string.IsNullOrEmpty(text), tone);

        IEnumerable<PreviewBlock> Reasoning(string? reasoning) =>
            reasoning is null ? [] : [Block("Field_Reasoning", reasoning, FieldTone.Reasoning)];
    }

    private void SetRecord(RecordDetails? record)
    {
        Record = record;
        _crossIssues = [];
        Judgment = JudgmentDetails.None;
        IsPiiPreview = false;
        UpdatePiiPreview();
        if (record is null)
        {
            Fields.Clear();
            Messages.Clear();
            _cleanHash = string.Empty;
            IsDirty = false;
            Reanalyze();
            UpdatePreferenceCompare();
        }
        else
        {
            LoadEditor(record);
        }
    }

    private void LoadEditor(RecordDetails record)
    {
        ResetWriting();
        _loadingEditor = true;
        try
        {
            Fields.Clear();
            Messages.Clear();
            switch (record.Payload)
            {
                case InstructionPayload p:
                    Fields.Add(CreateField("Field_Instruction", p.Instruction));
                    Fields.Add(CreateField("Field_Input", p.Input));
                    Fields.Add(new EditorField("Field_Reasoning", p.Reasoning ?? string.Empty, FieldTone.Reasoning, _localizer, OnEdited, isOptional: true));
                    Fields.Add(CreateField("Field_Output", p.Output, writeTarget: AIWriteTarget.Answer));
                    break;
                case ChatPayload p:
                    Fields.Add(CreateField("Field_SystemPrompt", p.SystemPrompt ?? string.Empty, FieldTone.System));
                    foreach (var message in p.Messages)
                    {
                        Messages.Add(CreateMessage(message.Role, message.Content, message.Reasoning));
                    }

                    break;
                case PreferencePayload p:
                    Fields.Add(CreateField("Field_Prompt", p.Prompt));
                    Fields.Add(CreateField("Field_Chosen", p.Chosen, FieldTone.Positive, AIWriteTarget.Chosen));
                    Fields.Add(CreateField("Field_Rejected", p.Rejected, FieldTone.Negative, AIWriteTarget.Rejected));
                    break;
            }

            _cleanHash = record.ContentHash;
            IsDirty = false;
        }
        finally
        {
            _loadingEditor = false;
        }

        Reanalyze();
        UpdatePreferenceCompare();
    }

    private RecordPayload BuildPayload(RecordPayload original) => original switch
    {
        InstructionPayload => new InstructionPayload(
            Field("Field_Instruction").Text, Field("Field_Input").Text, Field("Field_Output").Text, Field("Field_Reasoning").Text),
        ChatPayload => new ChatPayload(Fields[0].Text, Messages.Select(m => new ChatMessage(m.Role, m.Text, m.Reasoning)).ToList()),
        PreferencePayload => new PreferencePayload(Fields[0].Text, Fields[1].Text, Fields[2].Text),
        _ => original,
    };

    private void OnEdited()
    {
        if (!_loadingEditor && Record is { } record)
        {
            IsDirty = BuildPayload(record.Payload).ComputeContentHash() != _cleanHash;
            Reanalyze();
            UpdatePreferenceCompare();
        }
    }

    private EditorField Field(string labelKey) => Fields.First(f => f.LabelKey == labelKey);

    private EditorField CreateField(string labelKey, string text, FieldTone tone = FieldTone.Neutral, AIWriteTarget? writeTarget = null) =>
        new(labelKey, text, tone, _localizer, OnEdited, writeTarget);

    private EditorMessage CreateMessage(ChatRole role, string text, string? reasoning = null) =>
        new(role, text, _localizer, OnEdited, message =>
        {
            Messages.Remove(message);
            OnEdited();
        }, reasoning);

    private List<InfoItem> BuildInfo(RecordDetails record)
    {
        // A variation names the record it varies; its reference is that record's number.
        var reference = VariantOf.FromMetadata(record.MetadataJson) is { } family
            ? _localizer.Format("Meta_VariationOf", family.Ordinal)
            : record.Lineage.SourceReference;
        var items = new List<InfoItem>
        {
            new(_localizer["Meta_Source"], Labels.For(_localizer, record.Lineage.SourceKind) + Suffix(reference)),
        };
        if (Source is { } source)
        {
            var place = SourceText.Location(_localizer, source.Unit, source.First, source.Last);
            items.Add(new InfoItem(_localizer["Meta_Section"], source.Section.Length > 0 ? $"{source.Section} · {place}" : place));
        }

        if (record.Lineage.GeneratedByModel is { } model)
        {
            items.Add(new InfoItem(_localizer["Meta_GeneratedBy"], model));
        }

        items.Add(new InfoItem(_localizer["Meta_Created"], record.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        items.Add(new InfoItem(_localizer["Meta_Updated"], record.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        items.Add(new InfoItem(_localizer["Meta_ContentHash"], record.ContentHash[..12], IsMonospace: true));
        return items;

        static string Suffix(string? reference) => reference is null ? string.Empty : $" ({reference})";
    }

    private void Refresh()
    {
        Reanalyze();
        RefreshJudgmentTexts();
        UpdatePiiPreview();
        UpdatePreferenceCompare();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(BatchTitle));
        OnPropertyChanged(nameof(BatchCounts));
        foreach (var field in Fields)
        {
            field.RefreshLabel();
        }

        foreach (var message in Messages)
        {
            message.RefreshLabel();
        }
    }
}
