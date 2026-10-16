using System.Collections.ObjectModel;
using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels;

public enum FieldTone
{
    Neutral,
    System,
    Positive,
    Negative,

    /// <summary>The thinking before an answer (ADR-052).</summary>
    Reasoning,
}

/// <summary>
/// An editable text field of a record: instruction, input, reasoning, output, system prompt,
/// prompt, chosen or rejected answer.
/// </summary>
public sealed partial class EditorField : ViewModelBase
{
    private readonly Localizer _localizer;
    private readonly Action _edited;

    /// <param name="writeTarget">What a model can write into this field, if anything.</param>
    /// <param name="isOptional">Shown only once it has text or is added; removing it clears it.</param>
    public EditorField(string labelKey, string text, FieldTone tone, Localizer localizer, Action edited, AIWriteTarget? writeTarget = null, bool isOptional = false)
    {
        LabelKey = labelKey;
        Tone = tone;
        _localizer = localizer;
        _edited = edited;
        WriteTarget = writeTarget;
        IsOptional = isOptional;
        IsOpen = !isOptional || text.Length > 0;
        Text = text;
    }

    public string LabelKey { get; }

    /// <summary>
    /// A field most records leave empty, such as the reasoning: closed until it is added.
    /// </summary>
    public bool IsOptional { get; }

    /// <summary>
    /// The field is shown; a closed optional field shows only the button that adds it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>
    /// What a model can write into this field (the answer side), or <c>null</c>.
    /// </summary>
    public AIWriteTarget? WriteTarget { get; }

    public bool CanBeWritten => WriteTarget is not null;

    /// <summary>
    /// A model is writing into the field; it cannot be typed into meanwhile.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWriting { get; set; }

    public string Label => _localizer[LabelKey];

    public FieldTone Tone { get; }

    public bool IsPositive => Tone == FieldTone.Positive;

    public bool IsNegative => Tone == FieldTone.Negative;

    public bool IsSystem => Tone == FieldTone.System;

    public bool IsReasoning => Tone == FieldTone.Reasoning;

    [ObservableProperty]
    public partial string Text { get; set; }

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    [RelayCommand]
    private void Open() => IsOpen = true;

    /// <summary>
    /// Removes an optional field: its text is cleared and it closes.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        Text = string.Empty;
        IsOpen = !IsOptional;
    }

    partial void OnTextChanged(string value) => _edited?.Invoke();
}

/// <summary>
/// An editable chat turn. Its role can be switched between user and assistant, and it can be
/// removed; an assistant turn can have reasoning before its answer.
/// </summary>
public sealed partial class EditorMessage : ViewModelBase
{
    private readonly Localizer _localizer;
    private readonly Action _edited;
    private readonly Action<EditorMessage> _remove;

    /// <param name="calls">The turn's function calls; <c>null</c> when the record cannot call functions (a chat).</param>
    public EditorMessage(ChatRole role, string text, Localizer localizer, Action edited, Action<EditorMessage> remove, string? reasoning = null, IEnumerable<ToolCall>? calls = null)
    {
        _localizer = localizer;
        _edited = edited;
        _remove = remove;
        Role = role;
        Text = text;
        Reasoning = reasoning ?? string.Empty;
        IsReasoningOpen = reasoning is not null;
        CanCallTools = calls is not null;
        foreach (var call in calls ?? [])
        {
            Calls.Add(new EditorToolCall(call.Name, call.Arguments, call.Result, edited, RemoveCall));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsUser), nameof(IsAssistant), nameof(ShowReasoning), nameof(CanAddReasoning), nameof(ShowCalls))]
    public partial ChatRole Role { get; set; }

    /// <summary>
    /// The functions an assistant turn of a tool calling record calls (SDD §4.4).
    /// </summary>
    public ObservableCollection<EditorToolCall> Calls { get; } = [];

    /// <summary>
    /// The record can call functions: its assistant turns show their calls and can add more.
    /// </summary>
    public bool CanCallTools { get; }

    public bool ShowCalls => CanCallTools && IsAssistant;

    /// <summary>
    /// The function calls of the turn as the record stores them; none for a user turn.
    /// </summary>
    public IReadOnlyList<ToolCall> ToolCalls => [.. Calls.Select(c => new ToolCall(c.Name, c.Arguments, c.Result))];

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>
    /// The thinking before the answer; kept only on assistant turns (ADR-052).
    /// </summary>
    [ObservableProperty]
    public partial string Reasoning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReasoning), nameof(CanAddReasoning))]
    public partial bool IsReasoningOpen { get; private set; }

    public bool ShowReasoning => IsAssistant && IsReasoningOpen;

    public bool CanAddReasoning => IsAssistant && !IsReasoningOpen;

    /// <summary>
    /// A model is writing this turn; it cannot be typed into meanwhile.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWriting { get; set; }

    public string Label => _localizer[$"Field_{Role}"];

    public bool IsUser => Role == ChatRole.User;

    public bool IsAssistant => Role == ChatRole.Assistant;

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    [RelayCommand]
    private void SwitchRole() => Role = Role == ChatRole.User ? ChatRole.Assistant : ChatRole.User;

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void AddCall()
    {
        Calls.Add(new EditorToolCall(string.Empty, "{}", null, _edited, RemoveCall));
        _edited();
    }

    [RelayCommand]
    private void AddReasoning() => IsReasoningOpen = true;

    [RelayCommand]
    private void RemoveReasoning()
    {
        Reasoning = string.Empty;
        IsReasoningOpen = false;
    }

    partial void OnRoleChanged(ChatRole value) => _edited?.Invoke();

    partial void OnTextChanged(string value) => _edited?.Invoke();

    partial void OnReasoningChanged(string value) => _edited?.Invoke();

    private void RemoveCall(EditorToolCall call)
    {
        Calls.Remove(call);
        _edited();
    }
}

/// <summary>
/// A function call of an assistant turn: the function's name, its arguments as JSON and what it
/// returned (SDD §4.4).
/// </summary>
public sealed partial class EditorToolCall : ViewModelBase
{
    private readonly Action _edited;
    private readonly Action<EditorToolCall> _remove;

    public EditorToolCall(string name, string arguments, string? result, Action edited, Action<EditorToolCall> remove)
    {
        _edited = edited;
        _remove = remove;
        Name = name;
        Arguments = arguments;
        Result = result ?? string.Empty;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Arguments { get; set; }

    /// <summary>
    /// What the function returned; empty when the example ends with the call.
    /// </summary>
    [ObservableProperty]
    public partial string Result { get; set; }

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnNameChanged(string value) => _edited?.Invoke();

    partial void OnArgumentsChanged(string value) => _edited?.Invoke();

    partial void OnResultChanged(string value) => _edited?.Invoke();
}

/// <summary>
/// A function the assistant of a tool calling record may call: its JSON definition, headed by
/// the function's name and description once it has them (SDD §4.4). A named tool is shown
/// folded, so the conversation stays in view; one without a name yet is open.
/// </summary>
public sealed partial class EditorTool : ViewModelBase
{
    private readonly Localizer _localizer;
    private readonly Action _edited;
    private readonly Action<EditorTool> _remove;

    public EditorTool(string json, Localizer localizer, Action edited, Action<EditorTool> remove)
    {
        _localizer = localizer;
        _edited = edited;
        _remove = remove;
        Json = json;
        IsExpanded = FunctionName is null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Description), nameof(HasDescription))]
    public partial string Json { get; set; }

    /// <summary>
    /// The JSON definition is shown and can be edited.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>
    /// The function's description, shown next to its name while the tool is folded.
    /// </summary>
    public string Description =>
        ToolCalls.Function(Json) is { } function && function.TryGetProperty("description", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String
            ? text.GetString()!
            : string.Empty;

    public bool HasDescription => Description.Length > 0;

    /// <summary>
    /// The tool's position, from 1, for a tool that has no name yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial int Number { get; set; }

    /// <summary>
    /// The function's name, or null while the JSON names none.
    /// </summary>
    public string? FunctionName => ToolCalls.NameOf(Json);

    public string Title => FunctionName ?? _localizer.Format("Editor_ToolNumber", Number);

    public void RefreshLabel() => OnPropertyChanged(nameof(Title));

    [RelayCommand]
    private void Remove() => _remove(this);

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    partial void OnJsonChanged(string value) => _edited?.Invoke();
}

/// <summary>
/// A read-only field of the personal-data preview.
/// </summary>
public sealed record PreviewBlock(string Label, string Text, bool IsEmpty, FieldTone Tone)
{
    public bool IsPositive => Tone == FieldTone.Positive;

    public bool IsNegative => Tone == FieldTone.Negative;

    public bool IsSystem => Tone == FieldTone.System;

    public bool IsReasoning => Tone == FieldTone.Reasoning;
}

/// <summary>
/// A labelled value in the record's info section.
/// </summary>
public sealed record InfoItem(string Label, string Value, bool IsMonospace = false);

/// <summary>
/// How many selected records have one status, for the batch panel.
/// </summary>
public sealed record StatusCount(RecordStatus Status, string Label, int Count);
