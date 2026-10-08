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
/// A labelled value in the record's info section.
/// </summary>
public sealed record InfoItem(string Label, string Value, bool IsMonospace = false);

/// <summary>
/// A piece of record content shown in the inspector: a field card or a chat message.
/// </summary>
public abstract record ContentBlock(string Label, string Value, bool IsEmpty);

public enum FieldTone
{
    Neutral,
    System,
    Positive,
    Negative,
}

public sealed record FieldBlock(string Label, string Value, bool IsEmpty, FieldTone Tone) : ContentBlock(Label, Value, IsEmpty)
{
    public bool IsPositive => Tone == FieldTone.Positive;

    public bool IsNegative => Tone == FieldTone.Negative;

    public bool IsSystem => Tone == FieldTone.System;
}

public sealed record MessageBlock(string Label, string Value, bool IsEmpty, ChatRole Role) : ContentBlock(Label, Value, IsEmpty)
{
    public bool IsUser => Role == ChatRole.User;

    public bool IsAssistant => Role == ChatRole.Assistant;

    public bool IsSystem => Role == ChatRole.System;
}

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
    [NotifyPropertyChangedFor(nameof(HasRecord), nameof(Title), nameof(Status), nameof(StatusLabel), nameof(Blocks), nameof(Info))]
    [NotifyPropertyChangedFor(nameof(ShowApprove), nameof(ShowSendToReview), nameof(ShowArchive), nameof(ShowRestore))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(SendToReviewCommand), nameof(ArchiveCommand), nameof(RestoreCommand), nameof(DeleteCommand))]
    public partial RecordDetails? Record { get; private set; }

    public bool HasRecord => Record is not null;

    public string Title => Record is { } record ? _localizer.Format("Details_Title", record.Ordinal) : string.Empty;

    public RecordStatus Status => Record?.Status ?? RecordStatus.Draft;

    public string StatusLabel => Record is { } record ? Labels.For(_localizer, record.Status) : string.Empty;

    public IReadOnlyList<ContentBlock> Blocks => Record is { } record ? BuildBlocks(record.Payload) : [];

    public IReadOnlyList<InfoItem> Info => Record is { } record ? BuildInfo(record) : [];

    // Only the actions that make sense for the current status are shown.
    public bool ShowApprove => CanApprove();

    public bool ShowSendToReview => CanSendToReview();

    public bool ShowArchive => CanArchive();

    public bool ShowRestore => CanRestore();

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
                _localizer["Action_Delete"],
                isDestructive: true))
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

    private List<ContentBlock> BuildBlocks(RecordPayload payload) => payload switch
    {
        InstructionPayload p =>
        [
            Field("Field_Instruction", p.Instruction),
            Field("Field_Input", p.Input),
            Field("Field_Output", p.Output),
        ],
        ChatPayload p =>
        [
            .. p.SystemPrompt is null ? [] : new ContentBlock[] { Field("Field_SystemPrompt", p.SystemPrompt, FieldTone.System) },
            .. p.Messages.Select(m => Message(m.Role, m.Content)),
        ],
        PreferencePayload p =>
        [
            Field("Field_Prompt", p.Prompt),
            Field("Field_Chosen", p.Chosen, FieldTone.Positive),
            Field("Field_Rejected", p.Rejected, FieldTone.Negative),
        ],
        _ => [],
    };

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

    private FieldBlock Field(string labelKey, string value, FieldTone tone = FieldTone.Neutral) =>
        string.IsNullOrEmpty(value)
            ? new FieldBlock(_localizer[labelKey], _localizer["Field_Empty"], IsEmpty: true, tone)
            : new FieldBlock(_localizer[labelKey], value, IsEmpty: false, tone);

    private MessageBlock Message(ChatRole role, string value) =>
        string.IsNullOrEmpty(value)
            ? new MessageBlock(_localizer[$"Field_{role}"], _localizer["Field_Empty"], IsEmpty: true, role)
            : new MessageBlock(_localizer[$"Field_{role}"], value, IsEmpty: false, role);

    private void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(Info));
    }
}
