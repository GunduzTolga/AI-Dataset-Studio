using System.Globalization;
using AIDatasetStudio.Desktop.Localization;

namespace AIDatasetStudio.Desktop.Tests.Localization;

public sealed class LocalizerTests
{
    [Theory]
    [InlineData("tr-TR", "tr")]
    [InlineData("tr", "tr")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("", "en")]
    public void Maps_any_culture_to_a_supported_language(string requested, string expected)
    {
        var culture = Localizer.MatchSupported(CultureInfo.GetCultureInfo(requested));

        Assert.Equal(expected, culture.Name);
    }

    [Fact]
    public void Returns_text_in_the_selected_language()
    {
        Assert.Equal("Ready", new Localizer(Localizer.English)["Status_Ready"]);
        Assert.Equal("Hazır", new Localizer(Localizer.Turkish)["Status_Ready"]);
    }

    [Fact]
    public void Turkish_capitals_keep_the_dotted_I()
    {
        Assert.Equal("GEZGİN", new Localizer(Localizer.Turkish)["Pane_Explorer"]);
    }

    [Fact]
    public void Unknown_keys_are_visible_instead_of_empty()
    {
        Assert.Equal("[No_Such_Key]", new Localizer(Localizer.English)["No_Such_Key"]);
    }
}
