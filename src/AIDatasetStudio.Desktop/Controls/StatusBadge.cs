using AIDatasetStudio.Domain.Enums;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;

namespace AIDatasetStudio.Desktop.Controls;

/// <summary>
/// A colored pill for a record status. Colors come from the theme through the
/// :draft / :review / :approved / :archived pseudo-classes (see Themes/Controls.axaml).
/// </summary>
[PseudoClasses(":draft", ":review", ":approved", ":archived")]
public sealed class StatusBadge : Border
{
    public static readonly StyledProperty<RecordStatus> StatusProperty =
        AvaloniaProperty.Register<StatusBadge, RecordStatus>(nameof(Status));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusBadge, string?>(nameof(Text));

    private readonly TextBlock _text = new();

    public StatusBadge()
    {
        Child = _text;
        UpdatePseudoClasses(Status);
    }

    public RecordStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusProperty)
        {
            UpdatePseudoClasses(change.GetNewValue<RecordStatus>());
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = change.GetNewValue<string?>();
        }
    }

    private void UpdatePseudoClasses(RecordStatus status)
    {
        PseudoClasses.Set(":draft", status == RecordStatus.Draft);
        PseudoClasses.Set(":review", status == RecordStatus.InReview);
        PseudoClasses.Set(":approved", status == RecordStatus.Approved);
        PseudoClasses.Set(":archived", status == RecordStatus.Archived);
    }
}
