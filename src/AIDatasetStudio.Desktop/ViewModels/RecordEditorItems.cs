using AIDatasetStudio.Application.AI;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Domain.Enums;
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

    public EditorMessage(ChatRole role, string text, Localizer localizer, Action edited, Action<EditorMessage> remove, string? reasoning = null)
    {
        _localizer = localizer;
        _edited = edited;
        _remove = remove;
        Role = role;
        Text = text;
        Reasoning = reasoning ?? string.Empty;
        IsReasoningOpen = reasoning is not null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsUser), nameof(IsAssistant), nameof(ShowReasoning), nameof(CanAddReasoning))]
    public partial ChatRole Role { get; set; }

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
