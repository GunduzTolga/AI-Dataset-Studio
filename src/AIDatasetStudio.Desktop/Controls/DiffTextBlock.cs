using AIDatasetStudio.Application.Versions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AIDatasetStudio.Desktop.Controls;

/// <summary>
/// Selectable text whose deleted and inserted parts have a red or green background
/// (<c>DiffDeletedBrush</c> / <c>DiffInsertedBrush</c> from the theme).
/// </summary>
public sealed class DiffTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<DiffSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<DiffTextBlock, IReadOnlyList<DiffSegment>?>(nameof(Segments));

    public IReadOnlyList<DiffSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty || change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild(); // the theme's brushes can be found now
    }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        foreach (var segment in Segments ?? [])
        {
            var run = new Run(segment.Text);
            if (segment.Kind != DiffKind.Equal)
            {
                run.Background = Brush(segment.Kind == DiffKind.Inserted ? "DiffInsertedBrush" : "DiffDeletedBrush");
            }

            inlines.Add(run);
        }

        Inlines = inlines;
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
}
