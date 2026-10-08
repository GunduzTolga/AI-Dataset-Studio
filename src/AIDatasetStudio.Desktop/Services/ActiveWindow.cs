using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AIDatasetStudio.Desktop.Services;

/// <summary>
/// Finds the window that should own a new dialog or file picker.
/// </summary>
internal static class ActiveWindow
{
    /// <summary>
    /// The most recently opened visible window (a wizard open over the main window owns its own
    /// pickers), or <paramref name="fallback"/> when there is none (e.g. in tests).
    /// </summary>
    public static Window? Find(Func<Window?> fallback) =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.LastOrDefault(w => w.IsVisible)
        ?? fallback();
}
