using CommunityToolkit.Mvvm.ComponentModel;

namespace AIDatasetStudio.Desktop.ViewModels.Dialogs;

public sealed record DialogOption(object Value, string Label);

/// <summary>
/// One small dialog for the three questions the app asks: a name, a choice, or a confirmation.
/// </summary>
public sealed partial class InputDialogViewModel : ViewModelBase
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public required string ConfirmText { get; init; }

    public required string CancelText { get; init; }

    /// <summary>
    /// Shows the text box; the answer must then be non-blank and at most <see cref="MaxLength"/> characters.
    /// </summary>
    public bool ShowText { get; init; }

    /// <summary>
    /// The confirming action deletes data; the button is styled as dangerous.
    /// </summary>
    public bool IsDestructive { get; init; }

    public bool IsNotDestructive => !IsDestructive;

    public int MaxLength { get; init; } = 200;

    public IReadOnlyList<DialogOption> Options { get; init; } = [];

    public bool ShowOptions => Options.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial DialogOption? SelectedOption { get; set; }

    public bool CanConfirm =>
        (!ShowText || (!string.IsNullOrWhiteSpace(Text) && Text.Trim().Length <= MaxLength))
        && (!ShowOptions || SelectedOption is not null);
}
