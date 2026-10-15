using System.Diagnostics;
using System.Text;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Desktop.Services;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

// Writing with AI (SDD §17, M12): the model of the default connection writes the answer side of
// the shown record into its field while the user watches; the edit is saved as usual and the
// model is noted in the record's lineage.
public sealed partial class RecordDetailsViewModel
{
    // The editor shows written text this often at most, so long answers do not redraw on every piece.
    private static readonly TimeSpan WriteRefresh = TimeSpan.FromMilliseconds(40);

    private CancellationTokenSource? _writing;

    /// <summary>
    /// The model that wrote into the unsaved edit, noted in the lineage when it is saved.
    /// </summary>
    private string? _writtenBy;

    /// <summary>
    /// Raised when the user wants to set up an AI connection.
    /// </summary>
    public event EventHandler? AIConnectionsRequested;

    /// <summary>
    /// Raised when the user wants variations of the shown or selected records (SDD §17.6).
    /// </summary>
    public event EventHandler? VariationsRequested;

    /// <summary>
    /// Whether variations can be generated of the dataset's records.
    /// </summary>
    public bool CanVary => Dataset is { } dataset && Variations.Supports(dataset.Type);

    /// <summary>
    /// The field being written, or <c>null</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWriting), nameof(WritingText))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(WriteMainCommand), nameof(StopWritingCommand), nameof(SaveCommand), nameof(JudgeCommand))]
    public partial AIWriteTarget? WritingTarget { get; private set; }

    public bool IsWriting => WritingTarget is not null;

    /// <summary>
    /// "Writing with qwen2.5:7b…".
    /// </summary>
    public string WritingText => _ai.Connection is { } connection ? _localizer.Format("Editor_Writing", connection.Model) : string.Empty;

    /// <summary>
    /// Why the model could not write, with a way to the AI connections.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAIError))]
    public partial string? AIError { get; private set; }

    public bool HasAIError => AIError is not null;

    /// <summary>
    /// The current or last writing; awaited by tests.
    /// </summary>
    public Task Writing { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Whether <paramref name="target"/> can be written into the shown record.
    /// </summary>
    private bool CanWrite(AIWriteTarget? target) =>
        target is { } t && !IsWriting && IsEditable && IsEditing && Record?.Payload is { } payload && AIPrompts.TargetsFor(payload.Type).Contains(t);

    /// <summary>
    /// Has the model write the answer, reply, chosen or rejected answer of the shown record.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task WriteAsync(AIWriteTarget? target) => target is { } t ? StartWriting(t) : Task.CompletedTask;

    private bool CanWriteMain() => IsWriting || (Record?.Payload is { } payload && CanWrite(AIPrompts.TargetsFor(payload.Type).FirstOrDefault()));

    /// <summary>
    /// Mod+G: writes the record's main answer side, or stops the writing in progress. It may run
    /// while it runs: that is how the shortcut stops it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWriteMain), AllowConcurrentExecutions = true)]
    private Task WriteMainAsync()
    {
        if (IsWriting)
        {
            StopWriting();
            return Writing;
        }

        return Record?.Payload is { } payload ? StartWriting(AIPrompts.TargetsFor(payload.Type)[0]) : Task.CompletedTask;
    }

    /// <summary>
    /// Stops the model; what it wrote so far stays in the field.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsWriting))]
    private void StopWriting() => _writing?.Cancel();

    [RelayCommand]
    private void OpenAIConnections()
    {
        AIError = null;
        AIConnectionsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void GenerateVariations() => VariationsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void DismissAIError() => AIError = null;

    /// <summary>
    /// Stops writing and waits for it, before the record changes.
    /// </summary>
    private async Task StopWritingAsync()
    {
        _writing?.Cancel();
        await Writing;
    }

    /// <summary>
    /// Forgets the model of a discarded or saved edit, and an old error.
    /// </summary>
    private void ResetWriting()
    {
        _writing?.Cancel();
        _writtenBy = null;
        AIError = null;
    }

    private Task StartWriting(AIWriteTarget target)
    {
        AIError = null;
        if (Record is not { } record)
        {
            return Task.CompletedTask;
        }

        if (_ai.Connection is not { } connection)
        {
            AIError = _localizer["Editor_NoAIConnection"];
            return Task.CompletedTask;
        }

        var payload = BuildPayload(record.Payload);
        if (AIAssistant.Prompt(payload, target, Dataset?.AIGuidance) is not { } messages)
        {
            AIError = _localizer[target == AIWriteTarget.Reply ? "Editor_WriteNeedsUserTurn" : "Editor_WriteNeedsQuestion"];
            return Task.CompletedTask;
        }

        var (field, message) = TargetOf(payload, target);
        var cancellation = _writing = new CancellationTokenSource();
        WritingTarget = target;
        Writing = RunAsync();
        return Writing;

        async Task RunAsync()
        {
            var previous = field?.Text ?? message!.Text;
            Set(string.Empty, writing: true);
            var text = new StringBuilder();
            var shown = Stopwatch.StartNew();
            try
            {
                await foreach (var piece in _ai.WriteAsync(connection, messages, cancellation.Token))
                {
                    text.Append(piece);
                    if (shown.Elapsed >= WriteRefresh)
                    {
                        Set(text.ToString(), writing: true);
                        shown.Restart();
                    }
                }

                _status.Set("Status_Written", connection.Model);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _status.Set("Status_WritingStopped");
            }
            catch (AIException ex)
            {
                // A failed answer is not kept: the field gets back what it had.
                text.Clear().Append(previous);
                AIError = _ai.Describe(ex, connection);
            }
            finally
            {
                var written = text.ToString();
                Set(written, writing: false);
                // Discarded or replaced meanwhile: the field is no longer part of the record.
                var current = field is not null ? Fields.Contains(field) : Messages.Contains(message!);
                if (current && written != previous && written.Length > 0)
                {
                    _writtenBy = connection.Model;
                }

                if (_writing == cancellation)
                {
                    _writing = null;
                }

                WritingTarget = null;
            }
        }

        void Set(string text, bool writing)
        {
            if (field is not null)
            {
                field.Text = text;
                field.IsWriting = writing;
            }
            else
            {
                message!.Text = text;
                message.IsWriting = writing;
            }
        }
    }

    /// <summary>
    /// The field the target is written into; for a reply, the last assistant turn or a new one.
    /// </summary>
    private (EditorField? Field, EditorMessage? Message) TargetOf(RecordPayload payload, AIWriteTarget target)
    {
        switch (target)
        {
            case AIWriteTarget.Reply:
                var index = AIPrompts.ReplyIndex((ChatPayload)payload);
                if (index < Messages.Count)
                {
                    return (null, Messages[index]);
                }

                var reply = CreateMessage(ChatRole.Assistant, string.Empty);
                Messages.Add(reply);
                return (null, reply);
            default:
                return (Fields.First(f => f.WriteTarget == target), null);
        }
    }
}
