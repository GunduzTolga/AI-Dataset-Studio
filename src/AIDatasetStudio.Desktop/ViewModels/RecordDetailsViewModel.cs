using System.Globalization;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

public sealed record DetailField(string Label, string Value, bool IsEmpty);

public sealed record RecordStatusChange(Guid RecordId, RecordStatus Status);

/// <summary>
/// Read-only view of the selected record with review actions. Editing arrives with the record
/// editor (M3).
/// </summary>
public sealed partial class RecordDetailsViewModel : ViewModelBase
{
    private readonly IRecordQueryService _queries;
    private readonly IRecordCommandService _commands;
    private readonly IDialogService _dialogs;
    private readonly StatusBarViewModel _status;
    private readonly Localizer _localizer;
    private int _generation;

    public RecordDetailsViewModel(
        IRecordQueryService queries,
        IRecordCommandService commands,
        IDialogService dialogs,
        StatusBarViewModel status,
        Localizer localizer)
    {
        _queries = queries;
        _commands = commands;
        _dialogs = dialogs;
        _status = status;
        _localizer = localizer;
        _localizer.CultureChanged += (_, _) => Refresh();
    }

    public event EventHandler<RecordStatusChange>? StatusChanged;

    public event EventHandler<Guid>? Deleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecord), nameof(Title), nameof(StatusLabel), nameof(Fields), nameof(Metadata))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SendToReviewCommand), nameof(ArchiveCommand), nameof(RestoreCommand), nameof(DeleteCommand))]
    public partial RecordDetails? Record { get; private set; }

    public bool HasRecord => Record is not null;

    public string Title => Record is { } record ? _localizer.Format("Details_Title", record.Ordinal) : string.Empty;

    public string StatusLabel => Record is { } record ? Labels.For(_localizer, record.Status) : string.Empty;

    public IReadOnlyList<DetailField> Fields => Record is { } record ? BuildFields(record.Payload) : [];

    public IReadOnlyList<DetailField> Metadata => Record is { } record ? BuildMetadata(record) : [];

    public async Task ShowAsync(Guid? recordId)
    {
        var generation = ++_generation;
        if (recordId is null)
        {
            Record = null;
            return;
        }

        try
        {
            var record = await _queries.GetAsync(recordId.Value);
            if (generation == _generation)
            {
                Record = record;
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
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
                _localizer["Action_Delete"]))
        {
            return;
        }

        try
        {
            await _commands.DeleteAsync([record.Id]);
            Record = null;
            _status.Set("Status_RecordDeleted", record.Ordinal);
            Deleted?.Invoke(this, record.Id);
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    private bool CanApprove() => Can(RecordStatus.Approved);

    private bool CanSendToReview() => Can(RecordStatus.InReview);

    private bool CanArchive() => Can(RecordStatus.Archived);

    // Restoring is the only way out of Archived; Archived → Draft is also allowed elsewhere, but
    // the button is only meaningful for archived records.
    private bool CanRestore() => Record?.Status == RecordStatus.Archived;

    private bool Can(RecordStatus target) =>
        Record is { } record && record.Status != target && RecordStatusTransitions.CanTransition(record.Status, target);

    private async Task ChangeStatusAsync(RecordStatus status)
    {
        if (Record is not { } record)
        {
            return;
        }

        try
        {
            var result = await _commands.SetStatusAsync([record.Id], status);
            if (result.Changed == 1)
            {
                Record = record with { Status = status };
                _status.Set("Status_RecordStatusChanged", record.Ordinal, Labels.For(_localizer, status));
                StatusChanged?.Invoke(this, new RecordStatusChange(record.Id, status));
            }
        }
        catch (Exception ex) when (_status.TryReport(ex))
        {
        }
    }

    private List<DetailField> BuildFields(RecordPayload payload) => payload switch
    {
        InstructionPayload p =>
        [
            Field("Field_Instruction", p.Instruction),
            Field("Field_Input", p.Input),
            Field("Field_Output", p.Output),
        ],
        ChatPayload p =>
        [
            .. p.SystemPrompt is null ? [] : new[] { Field("Field_SystemPrompt", p.SystemPrompt) },
            .. p.Messages.Select(m => Field($"Field_{m.Role}", m.Content)),
        ],
        PreferencePayload p =>
        [
            Field("Field_Prompt", p.Prompt),
            Field("Field_Chosen", p.Chosen),
            Field("Field_Rejected", p.Rejected),
        ],
        _ => [],
    };

    private List<DetailField> BuildMetadata(RecordDetails record)
    {
        var fields = new List<DetailField> { Text("Meta_Source", Labels.For(_localizer, record.Lineage.SourceKind) + Suffix(record.Lineage.SourceReference)) };
        if (record.Lineage.GeneratedByModel is { } model)
        {
            fields.Add(Text("Meta_GeneratedBy", model));
        }

        fields.Add(Text("Meta_Created", record.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        fields.Add(Text("Meta_Updated", record.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        fields.Add(Text("Meta_ContentHash", record.ContentHash[..12]));
        return fields;

        static string Suffix(string? reference) => reference is null ? string.Empty : $" ({reference})";
    }

    private DetailField Field(string labelKey, string value) =>
        string.IsNullOrEmpty(value)
            ? new DetailField(_localizer[labelKey], _localizer["Field_Empty"], IsEmpty: true)
            : new DetailField(_localizer[labelKey], value, IsEmpty: false);

    private DetailField Text(string labelKey, string value) => new(_localizer[labelKey], value, IsEmpty: false);

    private void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Fields));
        OnPropertyChanged(nameof(Metadata));
    }
}
