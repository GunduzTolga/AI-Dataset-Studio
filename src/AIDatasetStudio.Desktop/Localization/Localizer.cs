using System.ComponentModel;
using System.Globalization;

namespace AIDatasetStudio.Desktop.Localization;

/// <summary>
/// Source of UI text in the current UI language. XAML binds to its indexer (see <see cref="TrExtension"/>),
/// so switching the language updates every bound text without restarting.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    // Avalonia refreshes indexer bindings when the change names the indexer's CLR property
    // ("Item"); WPF's "Item[]" convention is ignored by Avalonia 12.
    private const string IndexerPropertyName = "Item";

    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    public static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr");

    internal Localizer(CultureInfo culture)
    {
        Culture = MatchSupported(culture);
    }

    /// <summary>
    /// The application-wide instance, starting in the OS UI language.
    /// </summary>
    public static Localizer Instance => InstanceHolder.Value;

    public static IReadOnlyList<CultureInfo> SupportedCultures { get; } = [English, Turkish];

    public CultureInfo Culture { get; private set; }

    public string this[string key] => Strings.ResourceManager.GetString(key, Culture) ?? $"[{key}]";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CultureChanged;

    /// <summary>
    /// Formats a resource string. Numbers and dates follow the user's regional settings,
    /// which may differ from the UI language.
    /// </summary>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>
    /// Makes <see cref="Culture"/> the UI culture of the process.
    /// </summary>
    public void Apply()
    {
        Strings.Culture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public void SetCulture(CultureInfo culture)
    {
        var supported = MatchSupported(culture);
        if (supported.Equals(Culture))
        {
            return;
        }

        Culture = supported;
        Apply();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerPropertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Maps any culture to a supported UI language: "tr-TR" → Turkish, anything without a translation → English.
    /// </summary>
    public static CultureInfo MatchSupported(CultureInfo culture)
    {
        for (var current = culture; !Equals(current, CultureInfo.InvariantCulture); current = current.Parent)
        {
            foreach (var supported in SupportedCultures)
            {
                if (string.Equals(current.Name, supported.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return supported;
                }
            }
        }

        return English;
    }

    // Created on first use, after all static members of Localizer are initialized.
    // An inline initializer would run in declaration order and could see SupportedCultures as null.
    private static class InstanceHolder
    {
        public static readonly Localizer Value = new(CultureInfo.CurrentUICulture);
    }
}
