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
}
