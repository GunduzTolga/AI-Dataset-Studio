using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Workspaces;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.ViewModels;

namespace AIDatasetStudio.Desktop.Tests.Localization;

/// <summary>
/// Keeps the English and Turkish resources complete and consistent with the code that uses them.
/// </summary>
public sealed partial class ResourceTests
{
    private static readonly IReadOnlyDictionary<string, string> English = Load(CultureInfo.InvariantCulture);
    private static readonly IReadOnlyDictionary<string, string> Turkish = Load(Localizer.Turkish);

    [Fact]
    public void Every_english_string_has_a_turkish_translation()
    {
        var missing = English.Keys.Where(key => !Turkish.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Turkish_has_no_strings_that_english_lacks()
    {
        var extra = Turkish.Keys.Where(key => !English.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();

        Assert.Empty(extra);
    }

    [Fact]
    public void No_string_is_blank()
    {
        var blank = English.Concat(Turkish).Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList();

        Assert.Empty(blank);
    }

    [Fact]
    public void Translations_use_the_same_format_placeholders()
    {
        var mismatched = English
            .Where(pair => Turkish.TryGetValue(pair.Key, out var turkish)
                && !Placeholders(pair.Value).SetEquals(Placeholders(turkish)))
            .Select(pair => pair.Key)
            .ToList();

        Assert.Empty(mismatched);
    }

    [Fact]
    public void Every_key_used_in_xaml_exists()
    {
        var usages = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "DesktopXaml"), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => TrUsage().Matches(File.ReadAllText(file)).Select(match => (File: Path.GetFileName(file), Key: match.Groups["key"].Value)))
            .ToList();
        var missing = usages
            .Where(usage => !English.ContainsKey(usage.Key))
            .Select(usage => $"{usage.File}: {usage.Key}")
            .ToList();

        Assert.NotEmpty(usages);
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_workspace_error_has_a_message()
    {
        foreach (var error in Enum.GetValues<WorkspaceFileError>())
        {
            Assert.Contains(UiErrors.WorkspaceErrorKey(error), English.Keys);
        }
    }

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var manager = new ResourceManager("AIDatasetStudio.Desktop.Localization.Strings", typeof(Localizer).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No resources for '{culture.Name}'.");
        return set.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\d+(,[^}]*)?(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{l:Tr\s+(?<key>[A-Za-z0-9_]+)\s*\}")]
    private static partial Regex TrUsage();
}
