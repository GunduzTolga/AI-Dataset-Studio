using System.Collections.ObjectModel;
using System.Globalization;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

/// <summary>
/// The inspector: edits the selected record with fields that match its type (SDD §6.1), offers
/// the review actions its status allows, and turns into a batch panel when several records are
/// selected. Edits are saved explicitly or automatically when another record is shown.
/// </summary>
public sealed partial class RecordDetailsViewModel : ViewModelBase
{
    private readonly IRecordQueryService _queries;
    private readonly RecordOperations _operations;
    private readonly IDialogService _dialogs;
    private readonly StatusBarViewModel _status;
    private readonly Localizer _localizer;
    private int _generation;
    private string _cleanHash = string.Empty;
    private bool _loadingEditor;
    private Guid _batchDatasetId;

    public RecordDetailsViewModel(
        IRecordQueryService queries,
        RecordOperations operations,
        IDialogService dialogs,
        StatusBarViewModel status,
        Localizer localizer)
    {
        _queries = queries;
        _operations = operations;
        _dialogs = dialogs;
        _status = status;
        _localizer = localizer;
        _localizer.CultureChanged += (_, _) => Refresh();
    }

    // ===== Single record =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecord), nameof(IsEmpty), nameof(Title), nameof(Status), nameof(StatusLabel), nameof(Info))]
    [NotifyPropertyChangedFor(nameof(IsChat), nameof(IsArchived), nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(ShowApprove), nameof(ShowSendToReview), nameof(ShowArchive), nameof(ShowRestore))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SendToReviewCommand), nameof(ArchiveCommand), nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(SaveCommand), nameof(AddMessageCommand))]
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

    // ===== Several records =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatch), nameof(IsEmpty), nameof(BatchTitle), nameof(BatchCounts))]
    [NotifyPropertyChangedFor(nameof(ShowBatchApprove), nameof(ShowBatchSendToReview), nameof(ShowBatchArchive), nameof(ShowBatchRestore))]
    public partial IReadOnlyList<RecordRowViewModel> BatchRows { get; private set; } = [];

    public bool IsBatch => BatchRows.Count > 1;

    public string BatchTitle => _localizer.Format("Batch_Title", BatchRows.Count);

    public IReadOnlyList<StatusCount> BatchCounts =>
        [.. BatchRows.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => new StatusCount(g.Key, Labels.For(_localizer, g.Key), g.Count()))];

    public bool ShowBatchApprove => BatchCan(RecordStatus.Approved);

    public bool ShowBatchSendToReview => BatchCan(RecordStatus.InReview);

    public bool ShowBatchArchive => BatchCan(RecordStatus.Archived);

    public bool ShowBatchRestore => BatchRows.Any(r => r.Status == RecordStatus.Archived);

    /// <summary>
    /// Shows one record (or nothing), saving unsaved edits of the previous one first.
    /// </summary>
    public async Task ShowAsync(Guid? recordId)
    {
        var generation = ++_generation;
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
        try
        {
            var fresh = await _queries.GetAsync(record.Id);
            if (generation == _generation)
            {
                SetRecord(fresh);
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
        if (!IsDirty || Record is not { } before || IsArchived)
        {
            return true;
        }

        var payload = BuildPayload(before.Payload);
        // Clean from now on, so a second call while this save runs does not save again.
        _cleanHash = payload.ComputeContentHash();
        IsDirty = false;
        try
        {
            if (await _operations.SaveAsync(before, payload))
            {
                _status.Set("Status_RecordSaved", before.Ordinal);
                if (Record?.Id == before.Id && await _queries.GetAsync(before.Id) is { } fresh && Record?.Id == before.Id)
                {
                    Record = fresh; // header, status and info; the fields already show this content
                }
            }

            return true;
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
            _cleanHash = before.ContentHash;
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

    private bool CanSave() => IsDirty && IsEditable;

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

    private void SetRecord(RecordDetails? record)
    {
        Record = record;
        if (record is null)
        {
            Fields.Clear();
            Messages.Clear();
            _cleanHash = string.Empty;
            IsDirty = false;
        }
        else
        {
            LoadEditor(record);
        }
    }

    private void LoadEditor(RecordDetails record)
    {
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
                    Fields.Add(CreateField("Field_Output", p.Output));
                    break;
                case ChatPayload p:
                    Fields.Add(CreateField("Field_SystemPrompt", p.SystemPrompt ?? string.Empty, FieldTone.System));
                    foreach (var message in p.Messages)
                    {
                        Messages.Add(CreateMessage(message.Role, message.Content));
                    }

                    break;
                case PreferencePayload p:
                    Fields.Add(CreateField("Field_Prompt", p.Prompt));
                    Fields.Add(CreateField("Field_Chosen", p.Chosen, FieldTone.Positive));
                    Fields.Add(CreateField("Field_Rejected", p.Rejected, FieldTone.Negative));
                    break;
            }

            _cleanHash = record.ContentHash;
            IsDirty = false;
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private RecordPayload BuildPayload(RecordPayload original) => original switch
    {
        InstructionPayload => new InstructionPayload(Fields[0].Text, Fields[1].Text, Fields[2].Text),
        ChatPayload => new ChatPayload(Fields[0].Text, Messages.Select(m => new ChatMessage(m.Role, m.Text)).ToList()),
        PreferencePayload => new PreferencePayload(Fields[0].Text, Fields[1].Text, Fields[2].Text),
        _ => original,
    };

    private void OnEdited()
    {
        if (!_loadingEditor && Record is { } record)
        {
            IsDirty = BuildPayload(record.Payload).ComputeContentHash() != _cleanHash;
        }
    }

    private EditorField CreateField(string labelKey, string text, FieldTone tone = FieldTone.Neutral) =>
        new(labelKey, text, tone, _localizer, OnEdited);

    private EditorMessage CreateMessage(ChatRole role, string text) =>
        new(role, text, _localizer, OnEdited, message =>
        {
            Messages.Remove(message);
            OnEdited();
        });

    private List<InfoItem> BuildInfo(RecordDetails record)
    {
        var items = new List<InfoItem>
        {
            new(_localizer["Meta_Source"], Labels.For(_localizer, record.Lineage.SourceKind) + Suffix(record.Lineage.SourceReference)),
        };
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
