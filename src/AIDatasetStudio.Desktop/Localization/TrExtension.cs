using Avalonia.Data;

namespace AIDatasetStudio.Desktop.Localization;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{l:Tr Status_Ready}"</c>.
/// Produces a binding, so the text follows <see cref="Localizer.SetCulture"/>.
/// </summary>
public sealed class TrExtension(string key)
{
    public string Key { get; } = key;

    public ReflectionBinding ProvideValue(IServiceProvider serviceProvider) =>
        new($"[{Key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay };
}
