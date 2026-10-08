using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace AIDatasetStudio.Desktop.Converters;

public static class LayoutConverters
{
    /// <summary>
    /// Aligns the user's chat bubbles to the right and everything else to the left.
    /// </summary>
    public static readonly IValueConverter RightIfTrue =
        new FuncValueConverter<bool, HorizontalAlignment>(isRight => isRight ? HorizontalAlignment.Right : HorizontalAlignment.Left);

    /// <summary>
    /// Height of a multi-line text box in dialogs; single-line boxes size to their content.
    /// </summary>
    public static readonly IValueConverter TallIfTrue =
        new FuncValueConverter<bool, double>(isTall => isTall ? 180 : double.NaN);

    /// <summary>
    /// A fraction 0–1 as a bar width in a 200 px track (token histogram); any non-zero share stays visible.
    /// </summary>
    public static readonly IValueConverter BarWidth =
        new FuncValueConverter<double, double>(fraction => fraction <= 0 ? 0 : Math.Max(2, Math.Round(fraction * 200)));
}
