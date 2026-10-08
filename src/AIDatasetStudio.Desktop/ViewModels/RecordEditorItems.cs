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
}

/// <summary>
/// An editable text field of a record: instruction, input, output, system prompt, prompt,
/// chosen or rejected answer.
/// </summary>
public sealed partial class EditorField : ViewModelBase
{
    private readonly Localizer _localizer;
    private readonly Action _edited;

    public EditorField(string labelKey, string text, FieldTone tone, Localizer localizer, Action edited)
    {
        LabelKey = labelKey;
        Tone = tone;
        _localizer = localizer;
        _edited = edited;
        Text = text;
    }

    public string LabelKey { get; }

    public string Label => _localizer[LabelKey];

    public FieldTone Tone { get; }

    public bool IsPositive => Tone == FieldTone.Positive;

    public bool IsNegative => Tone == FieldTone.Negative;

    public bool IsSystem => Tone == FieldTone.System;

    [ObservableProperty]
    public partial string Text { get; set; }

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    partial void OnTextChanged(string value) => _edited?.Invoke();
}

/// <summary>
/// An editable chat turn. Its role can be switched between user and assistant, and it can be removed.
/// </summary>
public sealed partial class EditorMessage : ViewModelBase
{
    private readonly Localizer _localizer;
    private readonly Action _edited;
    private readonly Action<EditorMessage> _remove;

    public EditorMessage(ChatRole role, string text, Localizer localizer, Action edited, Action<EditorMessage> remove)
    {
        _localizer = localizer;
        _edited = edited;
        _remove = remove;
        Role = role;
        Text = text;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsUser), nameof(IsAssistant))]
    public partial ChatRole Role { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; }

    public string Label => _localizer[$"Field_{Role}"];

    public bool IsUser => Role == ChatRole.User;

    public bool IsAssistant => Role == ChatRole.Assistant;

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    [RelayCommand]
    private void SwitchRole() => Role = Role == ChatRole.User ? ChatRole.Assistant : ChatRole.User;

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnRoleChanged(ChatRole value) => _edited?.Invoke();

    partial void OnTextChanged(string value) => _edited?.Invoke();
}

/// <summary>
/// A labelled value in the record's info section.
/// </summary>
public sealed record InfoItem(string Label, string Value, bool IsMonospace = false);

/// <summary>
/// How many selected records have one status, for the batch panel.
/// </summary>
public sealed record StatusCount(RecordStatus Status, string Label, int Count);
