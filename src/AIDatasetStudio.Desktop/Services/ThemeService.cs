using Avalonia.Styling;

namespace AIDatasetStudio.Desktop.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies the color theme to the running application and maps it to the stored setting.
/// </summary>
public static class ThemeService
{
    public static AppTheme Parse(string? value) => value switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };

    /// <returns>The setting value; <c>null</c> means "follow the OS".</returns>
    public static string? ToSetting(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => null,
    };

    public static void Apply(AppTheme theme)
    {
        if (Avalonia.Application.Current is { } application)
        {
            application.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}
